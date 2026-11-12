package main

import (
	"net"
	"sync"
)

// Closing the listening socket alone leaves accepted SOCKS sockets alive.
// Track both authenticated and not-yet-authenticated clients through teardown.
type proxyListener struct {
	net.Listener
	mu      sync.Mutex
	closed  bool
	clients map[*proxyConnection]struct{}
}
type proxyConnection struct {
	net.Conn
	owner *proxyListener
	once  sync.Once
}

func newProxyListener(listener net.Listener) *proxyListener {
	return &proxyListener{Listener: listener, clients: map[*proxyConnection]struct{}{}}
}
func (listener *proxyListener) Accept() (net.Conn, error) {
	connection, err := listener.Listener.Accept()
	if err != nil {
		return nil, err
	}
	listener.mu.Lock()
	defer listener.mu.Unlock()
	if listener.closed {
		connection.Close()
		return nil, net.ErrClosed
	}
	tracked := &proxyConnection{Conn: connection, owner: listener}
	listener.clients[tracked] = struct{}{}
	return tracked, nil
}
func (connection *proxyConnection) Close() error {
	err := connection.Conn.Close()
	connection.once.Do(func() {
		connection.owner.mu.Lock()
		delete(connection.owner.clients, connection)
		connection.owner.mu.Unlock()
	})
	return err
}
func (listener *proxyListener) Close() error {
	listener.mu.Lock()
	listener.closed = true
	clients := make([]*proxyConnection, 0, len(listener.clients))
	for connection := range listener.clients {
		clients = append(clients, connection)
	}
	listener.mu.Unlock()
	err := listener.Listener.Close()
	for _, connection := range clients {
		connection.Close()
	}
	return err
}
