// Command tsforward is a test-only tailnet node: it joins a control server with a pre-auth key
// through a userspace tsnet node and forwards tailnet TCP ports to loopback addresses. The headless
// Headscale connectivity check runs it as the "host" side so the real SharpRail host stays on loopback.
//
// Configuration comes from the environment (the auth key must not appear in a process listing):
// TSF_STATE, TSF_CONTROL_URL, TSF_AUTH_KEY, TSF_HOSTNAME. Arguments are "tailnetPort=host:port"
// forwards. Standard input carries commands, one per line: "drop" closes every forwarded connection,
// "pause" refuses new ones and drops the current ones, "resume" accepts again. Closing standard input
// stops the node, so the process never outlives its parent. Standard output carries one JSON line per
// event, starting with {"event":"ready","ip":...} and one {"event":"accept","port":...,"remote":...} per forwarded connection; tsnet's own log goes to standard error.
package main

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"os"
	"strings"
	"sync"
	"time"

	"tailscale.com/tsnet"
)

var (
	mu      sync.Mutex
	paused  bool
	tracked = map[net.Conn]struct{}{}
)

func emit(v map[string]any) {
	b, _ := json.Marshal(v)
	fmt.Println(string(b))
}

func track(c net.Conn) bool {
	mu.Lock()
	defer mu.Unlock()
	if paused {
		return false
	}
	tracked[c] = struct{}{}
	return true
}

func drop(pause *bool) {
	mu.Lock()
	defer mu.Unlock()
	if pause != nil {
		paused = *pause
	}
	for c := range tracked {
		c.Close()
		delete(tracked, c)
	}
}

func forward(l net.Listener, port, target string) {
	for {
		in, err := l.Accept()
		if err != nil {
			return
		}
		go func() {
			defer in.Close()
			if !track(in) {
				return
			}
			emit(map[string]any{"event": "accept", "port": port, "remote": in.RemoteAddr().String()})
			out, err := net.DialTimeout("tcp", target, 5*time.Second)
			if err != nil {
				return
			}
			defer out.Close()
			if !track(out) {
				return
			}
			done := make(chan struct{}, 2)
			go func() { io.Copy(out, in); done <- struct{}{} }()
			go func() { io.Copy(in, out); done <- struct{}{} }()
			<-done
		}()
	}
}

func main() {
	logf := func(f string, a ...any) { fmt.Fprintf(os.Stderr, f+"\n", a...) }
	server := &tsnet.Server{
		Dir:        os.Getenv("TSF_STATE"),
		Hostname:   os.Getenv("TSF_HOSTNAME"),
		ControlURL: os.Getenv("TSF_CONTROL_URL"),
		AuthKey:    os.Getenv("TSF_AUTH_KEY"),
		Ephemeral:  true,
		Logf:       logf,
		UserLogf:   logf,
	}
	defer server.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 90*time.Second)
	status, err := server.Up(ctx)
	cancel()
	if err != nil {
		emit(map[string]any{"event": "error", "message": err.Error()})
		os.Exit(1)
	}
	for _, arg := range os.Args[1:] {
		port, target, ok := strings.Cut(arg, "=")
		if !ok {
			emit(map[string]any{"event": "error", "message": "bad forward " + arg})
			os.Exit(2)
		}
		l, err := server.Listen("tcp", ":"+port)
		if err != nil {
			emit(map[string]any{"event": "error", "message": err.Error()})
			os.Exit(1)
		}
		go forward(l, port, target)
	}
	ip := ""
	if len(status.TailscaleIPs) > 0 {
		ip = status.TailscaleIPs[0].String()
	}
	emit(map[string]any{"event": "ready", "ip": ip, "name": strings.TrimRight(status.Self.DNSName, ".")})
	lines := bufio.NewScanner(os.Stdin)
	for lines.Scan() {
		yes, no := true, false
		switch strings.TrimSpace(lines.Text()) {
		case "drop":
			drop(nil)
		case "pause":
			drop(&yes)
		case "resume":
			drop(&no)
		}
		emit(map[string]any{"event": "ack", "command": strings.TrimSpace(lines.Text())})
	}
}
