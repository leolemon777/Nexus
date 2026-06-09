# Nexus Documentation

> Open-source industrial communication library — MIT licensed.

## Getting Started

- [HSL Migration Guide](../HSL_MIGRATION_GUIDE.md) — Migrate from HslCommunication to Nexus

## Core Infrastructure

- [Reconnect and Heartbeat](core/reconnect-heartbeat.md) — AutoReconnectGuard + HeartbeatGuard usage
- [Connection Pool](core/connection-pool.md) — ConnectionPool&lt;T&gt; lifecycle and thread safety
- [Address Context](core/address-context.md) — Runtime parameter overrides in address strings
- [Struct Mapping](core/struct-mapping.md) — PLC memory ↔ C# struct mapping with byte order control
- [IReadWriteDevice Audit](core/ireadwrite-device-audit.md) — Direct implementation classification and migration plan

## Protocol Documentation

### Modbus (Reference Package)

- [Overview](protocols/modbus/index.md) — Clients, features, quick start
- [Quickstart](protocols/modbus/quickstart.md) — First read in 5 minutes
- [Address Format](protocols/modbus/address-format.md) — Standard 5-digit addressing
- [Function Codes](protocols/modbus/function-codes.md) — FC01–FC23 + FC08 + FC43
- [Byte Order](protocols/modbus/byte-order.md) — ABCD/DCBA/BADC/CDAB
- [Packet Logging](protocols/modbus/packet-logging.md) — TX/RX capture and analysis
- [Complete Scope](protocols/modbus/complete-scope.md) — Full feature coverage matrix
- [Performance](protocols/modbus/performance.md) — Benchmark targets and methodology
- [Long-Run Stability](protocols/modbus/long-run.md) — Sustained polling and reconnect tests
- [Troubleshooting](protocols/modbus/troubleshooting.md) — Common errors and diagnostics

### Siemens

- [Overview](protocols/siemens/index.md) — S7, FetchWrite, PPI
- [S7 Communication](protocols/siemens/s7.md) — Primary Siemens client
- [PPI](protocols/siemens/ppi.md) — S7-200 serial
- [PPI Audit](protocols/siemens/ppi-audit.md) — Implementation review
- [Fetch/Write](protocols/siemens/fetch-write.md) — Legacy protocol
- [Reconnect and Heartbeat](protocols/siemens/reconnect-heartbeat.md) — S7-specific reliability
- [Troubleshooting](protocols/siemens/troubleshooting.md)

### Mitsubishi

- [Overview](protocols/mitsubishi/index.md) — MC3E Binary/ASCII/UDP, A1E, FX Serial

### Omron

- [Overview](protocols/omron/index.md) — FINS TCP/UDP, HostLink
- [Address Format](protocols/omron/address-format.md) — DM, CIO, WR, HR, EM areas
- [FINS Setup](protocols/omron/fins-setup.md) — Network/node/unit configuration
- [HostLink Coverage](protocols/omron/hostlink-coverage.md) — Serial and TCP HostLink
- [Troubleshooting](protocols/omron/troubleshooting.md) — FINS error codes

### Allen-Bradley

- [Overview](protocols/allenbradley/index.md) — CIP and PCCC
- [CIP Tag Syntax](protocols/allenbradley/cip-tag-syntax.md) — Tag names, arrays, UDT members
- [PCCC Coverage](protocols/allenbradley/pccc-coverage.md) — MicroLogix, PLC-5, SLC 500
- [UDT and Arrays](protocols/allenbradley/udt-arrays.md) — Honest scope assessment
- [Troubleshooting](protocols/allenbradley/troubleshooting.md) — CIP error codes, slot config

## Validation and Planning

- [Protocol Readiness](../PROTOCOL_READINESS.md) — Module-by-module readiness status
- [Real Device Validation](../REAL_DEVICE_VALIDATION.md) — Hardware evidence matrix
- [Release Checklist](../RELEASE_CHECKLIST.md) — Production release gates
- [Execution Plan](../EXECUTION_PLAN.md) — Strategic roadmap
