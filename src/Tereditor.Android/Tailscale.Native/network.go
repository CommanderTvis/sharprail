package main

import (
	"encoding/json"
	"errors"
	"net"
	"sync"

	"tailscale.com/net/netmon"
)

var networkSnapshot struct {
	sync.RWMutex
	interfaces []netmon.Interface
}

type networkUpdate struct {
	Interfaces []struct {
		Name      string   `json:"name"`
		Index     int      `json:"index"`
		MTU       int      `json:"mtu"`
		Addresses []string `json:"addresses"`
	} `json:"interfaces"`
	DefaultInterface string `json:"defaultInterface"`
	Gateway          string `json:"gateway"`
}

// Android app UIDs cannot enumerate netlink interfaces. Use the platform's
// ConnectivityManager snapshot through Tailscale's supported replacement hook.
func networkInterfaces() ([]netmon.Interface, error) {
	networkSnapshot.RLock()
	defer networkSnapshot.RUnlock()
	return append([]netmon.Interface{}, networkSnapshot.interfaces...), nil
}
func updateNetwork(input string) error {
	var update networkUpdate
	if err := json.Unmarshal([]byte(input), &update); err != nil {
		return err
	}
	interfaces := make([]netmon.Interface, 0, len(update.Interfaces))
	for _, item := range update.Interfaces {
		if item.Name == "" {
			return errors.New("network interface name is required")
		}
		mtu := item.MTU
		if mtu <= 0 {
			mtu = 1500
		}
		addresses := make([]net.Addr, 0, len(item.Addresses))
		for _, text := range item.Addresses {
			ip, prefix, err := net.ParseCIDR(text)
			if err != nil {
				return err
			}
			addresses = append(addresses, &net.IPNet{IP: ip, Mask: prefix.Mask})
		}
		interfaces = append(interfaces, netmon.Interface{Interface: &net.Interface{Name: item.Name, Index: item.Index, MTU: mtu, Flags: net.FlagUp | net.FlagRunning}, AltAddrs: addresses})
	}
	networkSnapshot.Lock()
	networkSnapshot.interfaces = interfaces
	networkSnapshot.Unlock()
	updatePlatformNetwork(update)
	if current != nil {
		if monitor, ok := current.server.Sys().NetMon.GetOK(); ok {
			monitor.InjectEvent()
		}
	}
	return nil
}

func suppliedNetworkAvailable() bool {
	networkSnapshot.RLock()
	defer networkSnapshot.RUnlock()
	for _, item := range networkSnapshot.interfaces {
		if item.Flags&net.FlagUp == 0 {
			continue
		}
		for _, address := range item.AltAddrs {
			if prefix, ok := address.(*net.IPNet); ok && !prefix.IP.IsUnspecified() && !prefix.IP.IsLoopback() && !prefix.IP.IsLinkLocalUnicast() {
				return true
			}
		}
	}
	return false
}
