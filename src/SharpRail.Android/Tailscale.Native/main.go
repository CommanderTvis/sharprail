package main

/*
#include <stdlib.h>
*/
import "C"

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
	"unsafe"

	"tailscale.com/client/local"
	"tailscale.com/envknob"
	"tailscale.com/ipn"
	"tailscale.com/ipn/ipnstate"
	"tailscale.com/logtail"
	"tailscale.com/net/socks5"
	"tailscale.com/tsnet"
)

type peer struct {
	Name    string `json:"name"`
	Address string `json:"address"`
}
type snapshot struct {
	State         string `json:"state"`
	AuthURL       string `json:"authUrl"`
	Online        bool   `json:"online"`
	Peers         []peer `json:"peers"`
	ProxyAddress  string `json:"proxyAddress"`
	ProxyUsername string `json:"proxyUsername"`
	ProxyPassword string `json:"proxyPassword"`
	Error         string `json:"error,omitempty"`
}
type node struct {
	server   *tsnet.Server
	client   *local.Client
	listener *proxyListener
	cancel   context.CancelFunc
	mu       sync.Mutex
	authURL  string
	username string
	password string
}

var calls sync.Mutex
var current *node

func quiet(string, ...any) {}
func secret() (string, error) {
	var b [24]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", err
	}
	return hex.EncodeToString(b[:]), nil
}
func start(input string) error {
	if current != nil {
		return nil
	}
	var options struct {
		StateDirectory string `json:"stateDirectory"`
		Hostname       string `json:"hostname"`
	}
	if err := json.Unmarshal([]byte(input), &options); err != nil {
		return err
	}
	if options.StateDirectory == "" {
		return errors.New("stateDirectory is required")
	}
	if err := os.MkdirAll(options.StateDirectory, 0700); err != nil {
		return err
	}
	if err := os.Chmod(options.StateDirectory, 0700); err != nil {
		return err
	}
	if err := prepareRuntime(options.StateDirectory); err != nil {
		return err
	}
	if options.Hostname == "" {
		options.Hostname = "sharprail-android"
	}
	username, err := secret()
	if err != nil {
		return err
	}
	password, err := secret()
	if err != nil {
		return err
	}
	server := &tsnet.Server{Dir: options.StateDirectory, Hostname: options.Hostname, Logf: quiet, UserLogf: quiet}
	client, err := server.LocalClient()
	if err != nil {
		server.Close()
		return err
	}
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		server.Close()
		return err
	}
	tracked := newProxyListener(listener)
	ctx, cancel := context.WithCancel(context.Background())
	n := &node{server: server, client: client, listener: tracked, cancel: cancel, username: username, password: password}
	current = n
	go func() {
		proxy := &socks5.Server{Dialer: server.Dial, Username: username, Password: password, Logf: quiet}
		_ = proxy.Serve(tracked)
	}()
	go n.watch(ctx)
	return nil
}
func (n *node) watch(ctx context.Context) {
	for ctx.Err() == nil {
		watcher, err := n.client.WatchIPNBus(ctx, ipn.NotifyInitialState)
		if err == nil {
			for {
				notification, err := watcher.Next()
				if err != nil {
					break
				}
				if notification.BrowseToURL != nil {
					n.mu.Lock()
					n.authURL = *notification.BrowseToURL
					n.mu.Unlock()
				}
			}
			watcher.Close()
		}
		select {
		case <-ctx.Done():
			return
		case <-time.After(time.Second):
		}
	}
}
func status() snapshot {
	result := snapshot{State: "Stopped", Peers: []peer{}}
	n := current
	if n == nil {
		return result
	}
	result.ProxyAddress = "socks5://" + n.listener.Addr().String()
	result.ProxyUsername = n.username
	result.ProxyPassword = n.password
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	state, err := n.client.Status(ctx)
	if err != nil {
		result.Error = err.Error()
		return result
	}
	result.State = state.BackendState
	result.Online = state.BackendState == "Running" && platformNetworkAvailable()
	n.mu.Lock()
	result.AuthURL = n.authURL
	n.mu.Unlock()
	if state.AuthURL != "" {
		result.AuthURL = state.AuthURL
	}
	if state.BackendState == "Running" {
		result.AuthURL = ""
	}
	for _, p := range state.Peer {
		if candidate, ok := discoveredPeer(p); ok {
			result.Peers = append(result.Peers, candidate)
		}
	}
	sort.Slice(result.Peers, func(i, j int) bool { return result.Peers[i].Name < result.Peers[j].Name })
	return result
}

func discoveredPeer(p *ipnstate.PeerStatus) (peer, bool) {
	if p == nil || !p.Online {
		return peer{}, false
	}
	name := strings.TrimRight(p.DNSName, ".")
	if name == "" {
		name = p.HostName
	}
	for _, address := range p.TailscaleIPs {
		if address.Is4() {
			return peer{name, address.String()}, true
		}
	}
	return peer{}, false
}

func call(operation, input string) snapshot {
	var err error
	switch operation {
	case "start":
		err = start(input)
	case "network":
		err = updateNetwork(input)
	case "status":
	case "login", "logout":
		if current == nil {
			err = errors.New("Tailscale has not been started")
			break
		}
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		if operation == "login" {
			err = current.client.StartLoginInteractive(ctx)
		} else {
			err = current.client.Logout(ctx)
			current.mu.Lock()
			current.authURL = ""
			current.mu.Unlock()
		}
		cancel()
	case "stop":
		if current != nil {
			n := current
			current = nil
			n.cancel()
			n.listener.Close()
			err = n.server.Close()
		}
	default:
		err = errors.New("Unknown Tailscale operation")
	}
	result := status()
	if err != nil {
		result.Error = err.Error()
	}
	return result
}

// prepareRuntime must precede backend construction: Android has no HOME or
// XDG cache directory, while upstream backend initialization requests LogsDir.
func prepareRuntime(stateDirectory string) error {
	logs := filepath.Join(stateDirectory, "logs")
	if err := os.MkdirAll(logs, 0700); err != nil {
		return err
	}
	if err := os.Chmod(logs, 0700); err != nil {
		return err
	}
	if err := os.Setenv("TS_LOGS_DIR", logs); err != nil {
		return err
	}
	envknob.SetNoLogsNoSupport()
	logtail.Disable()
	return nil
}

func recoverSnapshot(invoke func() snapshot) (result snapshot) {
	defer func() {
		if recover() != nil {
			result = snapshot{State: "Stopped", Peers: []peer{}, Error: "Embedded Tailscale failed during native initialization or operation"}
		}
	}()
	return invoke()
}

//export sr_ts_call
func sr_ts_call(operation *C.char, input *C.char) *C.char {
	calls.Lock()
	defer calls.Unlock()
	value := "{}"
	if input != nil {
		value = C.GoString(input)
	}
	result := recoverSnapshot(func() snapshot { return call(C.GoString(operation), value) })
	encoded, _ := json.Marshal(result)
	return C.CString(string(encoded))
}

//export sr_ts_free
func sr_ts_free(value unsafe.Pointer) { C.free(value) }
func main()                           {}
