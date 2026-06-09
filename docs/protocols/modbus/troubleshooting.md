# Modbus Troubleshooting

## Connection Fails

Check:

- IP address and TCP/UDP port.
- Firewall and routing.
- Station/unit id.
- Device maximum concurrent connections.
- Whether the device expects Modbus TCP, RTU-over-TCP, or a vendor gateway mode.

## Reads Return Wrong Values

Check:

- Address base: `40001` maps to offset `0`; short numeric `0` also maps to offset `0`.
- Register area: `3xxxx` is input register FC04; `4xxxx` is holding register FC03.
- `ByteOrder` for 32-bit and 64-bit values.
- Signed vs unsigned API choice.
- String encoding and padding.

## Writes Fail

Check:

- The target area is writable. `1xxxx` and `3xxxx` are read-only.
- The device permits writes from the current connection.
- Value range matches the register width.
- Coil writes use bool APIs or multiple-coil APIs.

## RTU Problems

Check:

- Baud rate, parity, data bits, stop bits.
- RS485 direction control in the adapter.
- Station id.
- CRC mismatch and line noise.
- Inter-frame timing.

## ASCII Problems

Check:

- Start delimiter `:`.
- CR/LF frame ending.
- LRC correctness.
- Uppercase hex encoding.

## RTU-over-TCP Problems

Check:

- Gateway mode. RTU-over-TCP sends RTU ADU frames over TCP without MBAP.
- Some gateways expect Modbus TCP instead.
- CRC pass-through expectations.

## Required Bug Report Data

When reporting an issue, include:

- Nexus version or commit.
- Client class used.
- Device model and firmware.
- Address and data type.
- TX/RX packet log.
- Expected value and actual value.
- Whether the same operation works with the local virtual server.
