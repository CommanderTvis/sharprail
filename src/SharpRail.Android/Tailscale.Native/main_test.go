package main

import (
	"encoding/json"
	"io"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"strings"
	"tailscale.com/envknob"
	"tailscale.com/ipn/ipnstate"
	"tailscale.com/logpolicy"
	"tailscale.com/net/socks5"
	"testing"
	"time"
)

func TestStoppedAndInvalidOperations(t *testing.T) {
	for _, operation := range []string{"stop", "status"} {
		state := call(operation, "{}")
		if state.State != "Stopped" || state.Error != "" || state.ProxyPassword != "" || state.Peers == nil {
			t.Fatalf("invalid stopped response for %s", operation)
		}
	}
	for _, operation := range []string{"login", "logout", "unknown"} {
		if call(operation, "{}").Error == "" {
			t.Fatalf("missing error for %s", operation)
		}
	}
	if call("start", "{}").Error == "" {
		t.Fatal("missing state directory accepted")
	}
	encoded, err := json.Marshal(call("status", "{}"))
	if err != nil {
		t.Fatal(err)
	}
	var shape map[string]any
	if err = json.Unmarshal(encoded, &shape); err != nil {
		t.Fatal(err)
	}
	for _, field := range []string{"state", "authUrl", "online", "peers", "proxyAddress", "proxyUsername", "proxyPassword"} {
		if _, ok := shape[field]; !ok {
			t.Fatalf("missing ABI field %s", field)
		}
	}
}
func TestCredentialsAreDistinct(t *testing.T) {
	a, err := secret()
	if err != nil {
		t.Fatal(err)
	}
	b, err := secret()
	if err != nil {
		t.Fatal(err)
	}
	if len(a) != 48 || a == b {
		t.Fatal("invalid proxy credentials")
	}
}

func TestProxyRequiresAuthenticationAndClosesClients(t *testing.T) {
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	tracked := newProxyListener(listener)
	defer tracked.Close()
	done := make(chan struct{})
	go func() {
		defer close(done)
		proxy := socks5.Server{Username: "test-user", Password: "test-secret", Logf: quiet}
		proxy.Serve(tracked)
	}()
	client, err := net.Dial("tcp", listener.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	client.SetDeadline(time.Now().Add(time.Second))
	if _, err = client.Write([]byte{5, 1, 0}); err != nil {
		t.Fatal(err)
	}
	response := make([]byte, 2)
	if _, err = io.ReadFull(client, response); err != nil {
		t.Fatal(err)
	}
	if response[1] != 255 {
		t.Fatal("unauthenticated client accepted")
	}
	pending, err := net.Dial("tcp", listener.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer pending.Close()
	deadline := time.Now().Add(time.Second)
	for {
		tracked.mu.Lock()
		count := len(tracked.clients)
		tracked.mu.Unlock()
		if count > 0 {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("client not accepted")
		}
		time.Sleep(time.Millisecond)
	}
	tracked.Close()
	pending.SetReadDeadline(time.Now().Add(time.Second))
	if _, err = pending.Read(make([]byte, 1)); err == nil {
		t.Fatal("pending proxy client survived teardown")
	}
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("proxy listener survived teardown")
	}
}

func TestDiscoveryUsesOwnPeerDNSAndIPv4(t *testing.T) {
	p := &ipnstate.PeerStatus{Online: true, HostName: "duplicate", DNSName: "unique.tailnet.ts.net.", TailscaleIPs: []netip.Addr{netip.MustParseAddr("fd7a:115c:a1e0::1"), netip.MustParseAddr("100.64.0.2")}}
	got, ok := discoveredPeer(p)
	if !ok || got.Name != "unique.tailnet.ts.net" || got.Address != "100.64.0.2" {
		t.Fatalf("bad peer: %+v", got)
	}
	p.DNSName = ""
	got, ok = discoveredPeer(p)
	if !ok || got.Name != "duplicate" {
		t.Fatal("missing host name fallback")
	}
	p.TailscaleIPs = p.TailscaleIPs[:1]
	if _, ok = discoveredPeer(p); ok {
		t.Fatal("IPv6-only peer accepted by IPv4 bootstrap")
	}
	p.Online = false
	if _, ok = discoveredPeer(p); ok {
		t.Fatal("offline peer discovered")
	}
}

func TestPlatformInterfaceSnapshot(t *testing.T) {
	if err := updateNetwork(`{"interfaces":[{"name":"wlan0","index":2,"mtu":1450,"addresses":["192.168.1.4/24","2001:db8::4/64"]}],"defaultInterface":"wlan0"}`); err != nil {
		t.Fatal(err)
	}
	interfaces, err := networkInterfaces()
	if err != nil || len(interfaces) != 1 {
		t.Fatal("missing platform interface")
	}
	addresses, err := interfaces[0].Addrs()
	if err != nil || len(addresses) != 2 || addresses[0].String() != "192.168.1.4/24" {
		t.Fatal("platform addresses not preserved")
	}
	if interfaces[0].MTU != 1450 || interfaces[0].Name != "wlan0" {
		t.Fatal("platform metadata not preserved")
	}
	if err := updateNetwork(`{"interfaces":[{"name":"bad","addresses":["invalid"]}]}`); err == nil {
		t.Fatal("malformed interface accepted")
	}
	if err := updateNetwork(`{"interfaces":[]}`); err != nil {
		t.Fatal(err)
	}
	interfaces, err = networkInterfaces()
	if err != nil || len(interfaces) != 0 {
		t.Fatal("offline snapshot queried OS interfaces")
	}
}

func TestSuppliedPhysicalNetworkAvailability(t *testing.T) {
	for _, test := range []struct {
		input     string
		available bool
	}{
		{`{"interfaces":[]}`, false},
		{`{"interfaces":[{"name":"lo","addresses":["127.0.0.1/8","::1/128"]}]}`, false},
		{`{"interfaces":[{"name":"wlan0","addresses":["0.0.0.0/0","fe80::1/64"]}]}`, false},
		{`{"interfaces":[{"name":"wlan0","addresses":["192.168.1.4/24"]}]}`, true},
		{`{"interfaces":[{"name":"cellular","addresses":["2001:db8::4/64"]}]}`, true},
	} {
		if err := updateNetwork(test.input); err != nil {
			t.Fatal(err)
		}
		if suppliedNetworkAvailable() != test.available {
			t.Fatalf("incorrect network availability for %s", test.input)
		}
	}
	updateNetwork(`{"interfaces":[]}`)
}

func TestPrivateRuntimeDirectoryAndUploadOptOut(t *testing.T) {
	t.Setenv("HOME", "")
	t.Setenv("XDG_CACHE_HOME", "")
	t.Setenv("TS_LOGS_DIR", "")
	directory := t.TempDir()
	if err := prepareRuntime(directory); err != nil {
		t.Fatal(err)
	}
	if logpolicy.LogsDir(quiet) != filepath.Join(directory, "logs") {
		t.Fatal("upstream logs escaped app-private state")
	}
	info, err := os.Stat(filepath.Join(directory, "logs"))
	if err != nil || info.Mode().Perm() != 0700 {
		t.Fatal("log directory is not private")
	}
	if !envknob.NoLogsNoSupport() {
		t.Fatal("cloud log uploads were not disabled")
	}
}
func TestSynchronousPanicBecomesError(t *testing.T) {
	result := recoverSnapshot(func() snapshot { panic("secret detail must not cross ABI") })
	if result.Error == "" || strings.Contains(result.Error, "secret") {
		t.Fatal("panic not safely represented")
	}
}
