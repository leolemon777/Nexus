# Omron FINS Setup Guide

> Last updated: 2026-06-09

## Overview

FINS (Factory Interface Network Service) is Omron's proprietary protocol for PLC communication. This guide covers network setup, node configuration, and the TCP/UDP handshake flow.

## Network Architecture

```
PC (Nexus) ──── TCP/UDP ────> Omron PLC
  IP: 192.168.1.100              IP: 192.168.1.10
  Port: any                      Port: 9600 (FINS default)
  FINS Node: auto-assigned       FINS Node: configured in PLC
```

## TCP Connection Handshake

FINS TCP uses a two-step connection process:

1. **TCP Connect**: Standard TCP connection to port 9600.
2. **FINS Handshake**: Nexus sends a FINS node registration request; PLC responds with the client's FINS node number.

The `FinsTcpClient` handles this automatically during `Connect()`:

```csharp
using var client = new FinsTcpClient("192.168.1.10", 9600);
var result = client.Connect();
// TCP connect + FINS handshake done automatically
```

## PLC Configuration

### FINS TCP Settings (CX-Programmer / CX-Designer)

1. Open the PLC's built-in Ethernet port settings.
2. Set **IP Address** (e.g., 192.168.1.10, Subnet: 255.255.255.0).
3. Enable **FINS/TCP** service.
4. Set **FINS/TCP Port**: 9600 (default).
5. Configure **IP Address Table** or **Automatic allocation** for FINS node assignment.

### FINS UDP Settings

1. Enable **FINS/UDP** service.
2. Set the **UDP Port**: 9600 (default).
3. Configure the **FINS/UDP IP Address Table** to allow the PC's IP.

### Key Parameters

| Parameter | Where to Set | Typical Value | Notes |
|-----------|-------------|---------------|-------|
| IP Address | PLC Ethernet settings | 192.168.1.10 | Must be reachable from PC. |
| Subnet Mask | PLC Ethernet settings | 255.255.255.0 | Must match PC's subnet. |
| FINS/TCP Port | PLC FINS settings | 9600 | Nexus default. |
| FINS/UDP Port | PLC FINS settings | 9600 | Nexus default. |
| FINS Node (PLC) | PLC FINS settings | 1 | The PLC's own FINS node number. |
| IP Address Table | PLC FINS settings | Auto or manual | Maps remote IPs to FINS nodes. |

## Network/Node/Unit Addressing

FINS uses three-level addressing:

| Level | Name | Description |
|-------|------|-------------|
| Network | `NetworkNo` | 0 = local network, 1+ = remote network via FINS gateway. |
| Node | `DestNode` | The PLC's FINS node number (typically 1 for first PLC). |
| Unit | `DestUnit` | 0 = CPU, 0x10+ = CPU bus unit. |

For most single-PLC setups, use defaults (Network=0, Node=auto-assigned by handshake, Unit=0).

### TCP Client

Node assignment is automatic via FINS TCP handshake. No manual configuration needed.

```csharp
var client = new FinsTcpClient("192.168.1.10", 9600);
// Network, Node, Unit are set automatically during Connect()
```

### UDP Client

For UDP, the client can optionally set source and destination nodes:

```csharp
var client = new FinsUdpClient("192.168.1.10", 9600);
// Set FINS addressing if PLC expects specific node numbers
client.DestNode = 1;
```

### FINS Serial

Serial connections need explicit node configuration:

```csharp
Stream serialStream = GetSerialStream(); // RS-232/RS-485
var client = new FinsSerialClient(serialStream, destNode: 1);
```

## Device Discovery

FINS UDP supports broadcast device discovery:

```csharp
using var client = new FinsUdpClient("192.168.1.255", 9600);
var devices = client.DiscoverDevices();
if (devices.IsSuccess)
{
    foreach (var dev in devices.Content)
    {
        Console.WriteLine($"Found: {dev.ModelName} at {dev.IpAddress}:{dev.Port}");
    }
}
```

## Connection Lifecycle

```csharp
// Connect
using var client = new FinsTcpClient("192.168.1.10", 9600);
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine($"Connection failed: {connect.Message}");
    return;
}

// Use
var read = client.ReadInt16("D100");

// Disconnect (automatic via Dispose)
client.Disconnect();
```

## Common Issues

| Issue | Cause | Fix |
|-------|-------|-----|
| "Connection refused" | FINS/TCP not enabled on PLC | Enable FINS/TCP in PLC Ethernet settings. |
| "FINS handshake failed" | IP Address Table not configured | Set auto-allocation or add PC IP to table. |
| "Address range error" (0x0302) | Address exceeds PLC memory size | Check PLC model's valid address range. |
| "Routing error" (0x0003) | Wrong network/node number | Use default (local network, auto node). |
| Timeout on first connect | Firewall or wrong IP | Verify IP, port 9600 not blocked. |
