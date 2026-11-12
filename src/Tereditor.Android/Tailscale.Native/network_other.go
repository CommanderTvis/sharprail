//go:build !android

package main

func updatePlatformNetwork(networkUpdate) {}

func platformNetworkAvailable() bool { return true }
