package main

import "tailscale.com/net/netmon"

func init() { netmon.RegisterInterfaceGetter(networkInterfaces) }
func updatePlatformNetwork(update networkUpdate) {
	netmon.UpdateLastKnownDefaultRouteInterface(update.DefaultInterface)
	netmon.UpdateLastKnownDefaultGateway(update.Gateway)
}

func platformNetworkAvailable() bool { return suppliedNetworkAvailable() }
