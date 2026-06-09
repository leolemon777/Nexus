# Contributing Protocol Implementations

This guide defines the engineering bar for Nexus protocol packages.
It is intended for open-source contributors and maintainers.

## Principles

- Keep Nexus clean-room. Do not copy HSL Communication source code or proprietary vendor SDK code.
- Prefer protocol specifications, device manuals, public packet captures, and original tests.
- Keep protocol packages `netstandard2.0` and dependency-light.
- Preserve the unified `OperateResult` model; do not throw for normal device/protocol failures.
- Keep changes small, reviewable, and scoped to one protocol family unless the change is intentionally shared.
- Do not claim production readiness without tests, docs, and real-device evidence.

## Package Shape

Each protocol package should follow the existing pattern:

- `{Protocol}Client.cs` for the main client.
- `{Protocol}Address.cs` for address parsing.
- `{Protocol}Model.cs` for enums and protocol data structures.
- `{Protocol}VirtualServer.cs` when a meaningful offline integration path is practical.
- `README.md` for NuGet package usage.
- `docs/protocols/{protocol}/` for full protocol documentation.

Most packages should reference only `Nexus.Core`. Cross-protocol references require a clear reason, such as a device family that intentionally embeds another protocol.

## netstandard2.0 Rules

Protocol libraries target `netstandard2.0`. Avoid APIs that are unavailable there:

- No `Span<T>` or `MemoryExtensions`.
- No `BitConverter.Int32BitsToSingle` or `SingleToInt32Bits`.
- No `string.Contains(string, StringComparison)`.
- No `IAsyncDisposable`.
- No direct `System.IO.Ports.SerialPort` dependency in protocol packages; use `ISerialPort`.

Use existing Core helpers first:

- `OperateResult` and `OperateResult<T>`.
- `TcpDeviceBase`, `SerialDeviceBase`, and `UdpDeviceBase`.
- `DataConverter`, `Endianness`, and `CrcCalculator`.
- `IBatchReadWrite` and `ISubscribeDevice` where the protocol supports them.
- `ILogger`/`IMessageLogger` and TX/RX events for diagnostics.

## Address Parsing

Address parsing must be deterministic and documented.

Required coverage:

- Valid examples for every supported memory area.
- Invalid examples with clear failure messages.
- Boundary addresses and lengths.
- Bit addressing where the protocol supports it.
- Relationship between public address strings and wire offsets.

If a client has compatibility behavior that differs from a standalone parser utility, document the client behavior and test the client path.

## Error Handling

Use `OperateResult.Failed(...)` or `OperateResult<T>.Failed(...)` for expected failures:

- Connection failures.
- Timeouts.
- Malformed responses.
- Protocol exception codes.
- Unsupported address areas or data types.
- Checksum failures.

Throw only for programmer errors such as invalid constructor dependencies or impossible internal state.

## Testing Requirements

New protocol work should add the narrowest meaningful tests first:

- Command/frame builder tests.
- Response parser tests for success, protocol exception, malformed frame, and checksum failure.
- Address parser tests.
- Byte order tests for multi-register numeric values.
- Integration tests against a virtual server when practical.
- Fake serial-port tests for serial protocols.
- Focused regression tests for every bug fix.

For tests that bind local ports, prefer dynamic ports. If fixed ports are unavoidable, disable collection parallelism for that test set or document the command required to run it reliably.

## Documentation Requirements

A protocol should not be promoted without docs covering:

- Client selection and transport choice.
- Connection parameters.
- Address format.
- Supported function/service codes or command types.
- Data type and byte-order behavior.
- Batch/read-write support.
- Virtual server or offline test path.
- Packet logging and troubleshooting.
- Real-device validation checklist.

Package README examples must compile against the public API.

## Real Device Validation

Record real-device evidence in `REAL_DEVICE_VALIDATION.md` before making production claims.

Minimum evidence:

- Device vendor, model, firmware, and connection path.
- Protocol client and package version.
- Tested read/write operations.
- Byte order and string encoding.
- Test duration and failure count.
- Sanitized packet log when possible.
- Known limitations found during validation.

Factory safety comes first. Never write to production equipment unless the operator has confirmed the address is safe.

## Review Checklist

Before merging protocol changes:

- Scope is limited to the intended protocol or shared helper.
- No unrelated formatting churn.
- No copied proprietary source.
- `netstandard2.0` constraints are respected.
- Tests cover success and failure paths.
- Docs and readiness tables reflect the real state.
- WPF changes, if any, avoid `.Result` and `.Wait()`.
- Default WPF theme remains `ThemeManager.Init("mono", "soft")`.

## Release Promotion

Use `RELEASE_CHECKLIST.md` for release gates.

Production Candidate means:

- Offline and integration tests are strong.
- Package metadata and README are ready.
- Protocol docs are usable by a new user.
- Diagnostics are available for field support.
- Real-device validation has been recorded.

Anything less should be labeled honestly as Usable, Experimental, Test Utility, or Needs Audit.
