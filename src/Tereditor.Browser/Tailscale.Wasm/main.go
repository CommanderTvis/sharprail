// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause
//
// Adapted for SharpRail from tailscale.com/cmd/tsconnect/wasm/wasm_js.go (v1.104.1): the same userspace
// engine and netstack, with a persistent (non-ephemeral) node, no SSH, no log uploads, and a TCP dial
// bridged to JavaScript so the client can run its WebSocket RPC over the tailnet.

// Command tailscale-wasm adds sharprailTailscale(config) to the JavaScript global scope. It returns a node
// object with start(), status(), login(), logout(), dial(address) and the connection methods read/write/close.
package main

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/netip"
	"sort"
	"strings"
	"sync"
	"syscall/js"
	"time"

	"tailscale.com/control/controlclient"
	"tailscale.com/envknob"
	"tailscale.com/ipn"
	"tailscale.com/ipn/ipnauth"
	"tailscale.com/ipn/ipnlocal"
	"tailscale.com/ipn/store/mem"
	"tailscale.com/logtail"
	"tailscale.com/net/netns"
	"tailscale.com/net/tsdial"
	"tailscale.com/tsd"
	"tailscale.com/types/logger"
	"tailscale.com/types/logid"
	"tailscale.com/wgengine"
	"tailscale.com/wgengine/netstack"
)

func main() {
	js.Global().Set("sharprailTailscale", js.FuncOf(func(this js.Value, args []js.Value) any {
		if len(args) != 1 {
			return js.Global().Get("Error").New("usage: sharprailTailscale(config)")
		}
		n, err := newNode(args[0])
		if err != nil {
			return js.Global().Get("Error").New(err.Error())
		}
		return n.exports()
	}))
	// Keep the Go runtime alive for the callbacks.
	<-make(chan bool)
}

type peer struct {
	Name    string `json:"name"`
	Address string `json:"address"`
}

type snapshot struct {
	State   string `json:"state"`
	AuthURL string `json:"authUrl"`
	Online  bool   `json:"online"`
	Peers   []peer `json:"peers"`
	Error   string `json:"error,omitempty"`
}

type node struct {
	lb         *ipnlocal.LocalBackend
	ns         *netstack.Impl
	controlURL string
	authKey    string
	hostname   string

	mu      sync.Mutex
	authURL string
	failure string
	started bool
	conns   map[int]net.Conn
	next    int
}

func quiet(string, ...any) {}

func newNode(config js.Value) (*node, error) {
	netns.SetEnabled(false)
	// No cloud log uploads: the same no-logs-no-support mode and kill switch as the Android bridge.
	envknob.SetNoLogsNoSupport()
	logtail.Disable()
	var store ipn.StateStore = new(mem.Store)
	if storage := config.Get("stateStorage"); !storage.IsUndefined() && !storage.IsNull() {
		store = &jsStateStore{storage}
	}
	n := &node{controlURL: ipn.DefaultControlURL, hostname: "sharprail-browser", conns: map[int]net.Conn{}}
	if value := config.Get("controlURL"); value.Type() == js.TypeString && value.String() != "" {
		n.controlURL = value.String()
	}
	if value := config.Get("authKey"); value.Type() == js.TypeString {
		n.authKey = value.String()
	}
	if value := config.Get("hostname"); value.Type() == js.TypeString && value.String() != "" {
		n.hostname = value.String()
	}
	var logf logger.Logf = quiet

	sys := tsd.NewSystem()
	sys.Set(store)
	dialer := &tsdial.Dialer{Logf: logf}
	dialer.SetBus(sys.Bus.Get())
	eng, err := wgengine.NewUserspaceEngine(logf, wgengine.Config{
		Dialer:        dialer,
		SetSubsystem:  sys.Set,
		ControlKnobs:  sys.ControlKnobs(),
		HealthTracker: sys.HealthTracker.Get(),
		ExtraRootCAs:  sys.ExtraRootCAs,
		Metrics:       sys.UserMetricsRegistry(),
		EventBus:      sys.Bus.Get(),
	})
	if err != nil {
		return nil, err
	}
	sys.Set(eng)
	ns, err := netstack.Create(logf, sys.Tun.Get(), eng, sys.MagicSock.Get(), dialer, sys.DNSManager.Get(), sys.ProxyMapper())
	if err != nil {
		return nil, err
	}
	sys.Set(ns)
	ns.ProcessLocalIPs = true
	ns.ProcessSubnets = true
	dialer.UseNetstackForIP = func(netip.Addr) bool { return true }
	dialer.NetstackDialTCP = func(ctx context.Context, dst netip.AddrPort) (net.Conn, error) {
		conn, err := ns.DialContextTCP(ctx, dst)
		if err != nil {
			return nil, err
		}
		return conn, nil
	}
	dialer.NetstackDialUDP = func(ctx context.Context, dst netip.AddrPort) (net.Conn, error) {
		conn, err := ns.DialContextUDP(ctx, dst)
		if err != nil {
			return nil, err
		}
		return conn, nil
	}
	sys.NetstackRouter.Set(true)
	sys.Tun.Get().Start()

	private, err := logid.NewPrivateID()
	if err != nil {
		return nil, err
	}
	// LoginDefault, not upstream's LoginEphemeral: the browser rejoins as the same device.
	lb, err := ipnlocal.NewLocalBackend(logf, private.Public(), sys, controlclient.LoginDefault)
	if err != nil {
		return nil, err
	}
	if err := ns.Start(lb); err != nil {
		return nil, err
	}
	n.lb = lb
	n.ns = ns
	return n, nil
}

func (n *node) exports() map[string]any {
	return map[string]any{
		"start": js.FuncOf(func(js.Value, []js.Value) any { n.start(); return nil }),
		"status": js.FuncOf(func(js.Value, []js.Value) any {
			bytes, _ := json.Marshal(n.status())
			return string(bytes)
		}),
		"login": js.FuncOf(func(js.Value, []js.Value) any {
			return promise(func() (any, error) {
				return nil, n.lb.StartLoginInteractive(context.Background())
			})
		}),
		"logout": js.FuncOf(func(js.Value, []js.Value) any {
			return promise(func() (any, error) {
				ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
				defer cancel()
				n.mu.Lock()
				n.authURL = ""
				n.mu.Unlock()
				return nil, n.lb.Logout(ctx, ipnauth.Self)
			})
		}),
		"dial": js.FuncOf(func(_ js.Value, args []js.Value) any {
			if len(args) != 2 {
				return rejected("usage: dial(address, timeoutMilliseconds)")
			}
			address, timeout := args[0].String(), args[1].Int()
			return promise(func() (any, error) { return n.dial(address, timeout) })
		}),
		"read": js.FuncOf(func(_ js.Value, args []js.Value) any {
			conn := n.conn(args[0].Int())
			return promise(func() (any, error) {
				if conn == nil {
					return nil, errors.New("the tailnet connection is closed")
				}
				buffer := make([]byte, 64*1024)
				count, err := conn.Read(buffer)
				if count > 0 {
					result := js.Global().Get("Uint8Array").New(count)
					js.CopyBytesToJS(result, buffer[:count])
					return result, nil
				}
				if err == io.EOF {
					return nil, nil
				}
				if err != nil {
					return nil, err
				}
				return js.Global().Get("Uint8Array").New(0), nil
			})
		}),
		"write": js.FuncOf(func(_ js.Value, args []js.Value) any {
			conn := n.conn(args[0].Int())
			bytes := make([]byte, args[1].Get("length").Int())
			js.CopyBytesToGo(bytes, args[1])
			return promise(func() (any, error) {
				if conn == nil {
					return nil, errors.New("the tailnet connection is closed")
				}
				_, err := conn.Write(bytes)
				return nil, err
			})
		}),
		"close": js.FuncOf(func(_ js.Value, args []js.Value) any {
			n.mu.Lock()
			conn := n.conns[args[0].Int()]
			delete(n.conns, args[0].Int())
			n.mu.Unlock()
			if conn != nil {
				conn.Close()
			}
			return nil
		}),
	}
}

func (n *node) start() {
	n.mu.Lock()
	if n.started {
		n.mu.Unlock()
		return
	}
	n.started = true
	n.mu.Unlock()
	n.lb.SetNotifyCallback(func(notification ipn.Notify) {
		if notification.BrowseToURL != nil {
			n.mu.Lock()
			n.authURL = *notification.BrowseToURL
			n.mu.Unlock()
		}
	})
	go func() {
		err := n.lb.Start(ipn.Options{
			UpdatePrefs: &ipn.Prefs{ControlURL: n.controlURL, RouteAll: false, WantRunning: true, Hostname: n.hostname},
			AuthKey:     n.authKey,
		})
		if err != nil {
			n.mu.Lock()
			n.failure = err.Error()
			n.mu.Unlock()
		}
	}()
}

func (n *node) status() snapshot {
	result := snapshot{State: n.lb.State().String(), Peers: []peer{}}
	n.mu.Lock()
	result.AuthURL = n.authURL
	result.Error = n.failure
	n.mu.Unlock()
	if result.State == ipn.Running.String() {
		result.AuthURL = ""
		result.Online = true
		for _, p := range n.lb.Status().Peer {
			if p == nil || !p.Online {
				continue
			}
			name := strings.TrimRight(p.DNSName, ".")
			if name == "" {
				name = p.HostName
			}
			for _, address := range p.TailscaleIPs {
				if address.Is4() {
					result.Peers = append(result.Peers, peer{name, address.String()})
					break
				}
			}
		}
	}
	sort.Slice(result.Peers, func(i, j int) bool { return result.Peers[i].Name < result.Peers[j].Name })
	return result
}

// dial opens a TCP stream to a numeric tailnet address over the netstack; names are never resolved here.
func (n *node) dial(address string, timeout int) (any, error) {
	target, err := netip.ParseAddrPort(address)
	if err != nil {
		return nil, errors.New("a tailnet dial needs a numeric address and port")
	}
	if n.lb.State() != ipn.Running {
		return nil, errors.New("Tailscale is not connected")
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(timeout)*time.Millisecond)
	defer cancel()
	conn, err := n.ns.DialContextTCP(ctx, target)
	if err != nil {
		return nil, err
	}
	n.mu.Lock()
	n.next++
	id := n.next
	n.conns[id] = conn
	n.mu.Unlock()
	return id, nil
}

func (n *node) conn(id int) net.Conn {
	n.mu.Lock()
	defer n.mu.Unlock()
	return n.conns[id]
}

// jsStateStore keeps the node identity in the page's synchronous state map, which persists it to IndexedDB.
type jsStateStore struct {
	storage js.Value
}

func (s *jsStateStore) ReadState(id ipn.StateKey) ([]byte, error) {
	value := s.storage.Call("getState", string(id))
	if value.Type() != js.TypeString || value.String() == "" {
		return nil, ipn.ErrStateNotExist
	}
	return hex.DecodeString(value.String())
}

func (s *jsStateStore) WriteState(id ipn.StateKey, bytes []byte) error {
	s.storage.Call("setState", string(id), hex.EncodeToString(bytes))
	return nil
}

func rejected(message string) js.Value {
	return js.Global().Get("Promise").Call("reject", js.Global().Get("Error").New(message))
}

// promise runs f on a goroutine, since a JavaScript callback must never block the Go event loop.
func promise(f func() (any, error)) js.Value {
	var handler js.Func
	handler = js.FuncOf(func(_ js.Value, args []js.Value) any {
		resolve, reject := args[0], args[1]
		go func() {
			defer handler.Release()
			if result, err := f(); err == nil {
				resolve.Invoke(result)
			} else {
				reject.Invoke(js.Global().Get("Error").New(err.Error()))
			}
		}()
		return nil
	})
	return js.Global().Get("Promise").New(handler)
}
