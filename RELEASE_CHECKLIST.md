# Nexus Release Checklist

This checklist is the release gate for open-source packages and the WPF debugger.
Do not claim production readiness for a protocol until the evidence items below are complete.

## 1. Scope

- Release name:
- Target version:
- Target date:
- Included packages:
- Included app build:
- Protocols promoted in this release:
- Known protocols excluded from production claims:

## 2. Repository Preflight

- `git status` reviewed; unrelated local changes identified.
- `CURRENT_GAP_MATRIX.md` reviewed for stale readiness claims.
- `PROTOCOL_READINESS.md` updated for all promoted protocols.
- `EXECUTION_PLAN.md` updated if milestone order changed.
- `implementation-notes.md` records meaningful tradeoffs, deviations, verification, and risks.
- No generated artifacts are outside the repository workspace.

## 3. Legal And Open Source Hygiene

- License file exists and matches package metadata.
- Third-party dependencies and licenses reviewed.
- No copied HSL Communication source code.
- HSL migration material is clean-room concept mapping only.
- Public docs avoid unsupported superiority claims.
- Repository URL, authors, tags, license, and README metadata are present in NuGet packages.

## 4. Build And Test Gates

- `dotnet restore Nexus.slnx`
- `dotnet build Nexus.slnx`
- `dotnet test Nexus.slnx`
- Focused protocol tests for every promoted package.
- WPF app build: `dotnet build src\Nexus.App`
- Package build for every promoted NuGet package.
- No new warnings without an explicit note.
- No protocol-level `NotImplementedException` in promoted packages.

## 5. Protocol Readiness Gates

For each promoted protocol:

- Offline command-building tests cover representative read/write operations.
- Offline response parser tests cover normal, exception, and malformed frames.
- Address parser tests cover valid, invalid, and boundary addresses.
- Byte order tests cover all relevant numeric types.
- Batch read/write tests exist where the protocol supports random access.
- Virtual server integration tests exist or a documented reason explains why not.
- Real-device validation evidence is recorded in `REAL_DEVICE_VALIDATION.md`.
- Long-run connection test result is recorded, including duration and failure count.
- Packet logs can be captured and shared without leaking sensitive plant data.

## 6. Documentation Gates

- Package README has a working quickstart.
- Protocol docs cover client selection, address format, function codes or service codes, byte order, logging, and troubleshooting.
- Migration guide covers the release's promoted protocols.
- Breaking changes and compatibility notes are documented.
- Examples compile against the released API.
- Chinese industrial deployment notes are included where relevant.

## 7. WPF Debugger Gates

- Default theme remains `ThemeManager.Init("mono", "soft")`.
- Connect/read/write flows tested manually for promoted protocols where no hardware is required.
- Error diagnostics remain readable in Chinese.
- Log export works.
- Packet capture/decode/export status is documented.
- No `.Result` or `.Wait()` introduced in UI code.

## 8. Package Verification

For each NuGet package:

- `dotnet pack <project>`
- `.nupkg` contains README, license metadata, repository metadata, and XML docs if enabled.
- Package installs into a fresh sample project.
- Sample project can connect to a virtual server or run offline examples.
- Version number is consistent across package metadata and release notes.

## 9. Release Notes

- Summary of shipped protocols and app features.
- Upgrade notes from the previous version.
- Known limitations and unsupported devices.
- Real-device validation table with concrete device models and dates.
- Security and safety notes for factory environments.
- Migration notes for HSL users.

## 10. Final Sign-off

- Build owner:
- Protocol owner:
- Docs owner:
- Validation owner:
- Release approver:
- Release tag:
- Package push time:
- Post-release smoke test result:

## Current Sprint 0 Status

- Modbus is the reference package candidate.
- Siemens, Mitsubishi, Omron, and AllenBradley remain usable but require deeper audit before production-candidate claims.
- Packet parser and WPF diagnostics are strategic differentiators and should be promoted only after tests and app integration land.
- Real-device validation is the main blocker for strong production claims.
