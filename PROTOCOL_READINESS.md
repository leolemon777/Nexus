# Protocol Readiness Table

> ⚠️ **SUPERSEDED** — 当前进度与权威数据见 [`OVERTAKE_HSL_PLAN.md`](OVERTAKE_HSL_PLAN.md)（顶部"当前进度快照"）与 [`CLAUDE.md`](CLAUDE.md)。本文件保留作历史记录。

> Workspace rule: all generated planning files for this workstream live under `E:\Desktop\Nexus2.0`.
>
> Last calibrated: 2026-06-09.

This table is a conservative readiness view. "Usable" means source and tests indicate a working implementation path, not that the protocol has been proven on real factory devices. "Production Candidate" requires explicit production gates and real-device evidence.

## Status Definitions

| Status | Meaning |
|--------|---------|
| Production Candidate | Strong tests and broad implementation exist; next step is production gate evidence, docs, and release packaging. |
| Usable | Basic or broad client functionality exists with tests; needs feature audit and documentation before production claims. |
| Experimental | Implementation exists but coverage, protocol depth, or integration evidence is not enough for confident adoption. |
| Test Utility | Useful support component, simulator, tooling, or non-core protocol module. |
| Needs Audit | Present in source but should not be promoted until feature scope is reviewed. |

## Top 5 Production Track

| Module | Scope | Status | Evidence | Missing Production Gate | Next Action |
|--------|-------|--------|----------|-------------------------|-------------|
| `Nexus.Modbus` | TCP, UDP, RTU, ASCII, ASCII-over-TCP, RTU-over-TCP, servers | Production Candidate | 210 tests; FC08/22/43; TCP/UDP batch and subscription; server classes; offline packet parser; package README wired; performance + long-run docs; shared WPF PacketRecorder | Real-device validation evidence | Make Modbus the reference package and release template. |
| `Nexus.Siemens` | S7, FetchWrite, PPI, virtual PLC | Usable | 122 tests; S7 batch; S7 String/WString; PLC control commands; virtual PLC; PPI audit docs | PPI production scope, S7 real-device matrix, reconnect/heartbeat guidance, HSL migration notes | Stabilize S7/PPI and write HSL migration notes. |
| `Nexus.Mitsubishi` | MC3E Binary/ASCII/UDP, A1E, FX serial (编程口+计算机链接), virtual servers | Usable | 257 tests; MC3E ASCII/UDP frame tests; FxLinkClient (计算机链接) consolidated; FX serial frame tests; support + complete-scope matrices; HSL migration chapter | Real-device validation evidence, FX serial production scope | Normalize MC family feature depth after docs baseline. |
| `Nexus.Omron` | FINS TCP/UDP, HostLink TCP/Serial, virtual servers | Usable | 123 tests; FINS TCP/UDP and HostLink batch; virtual servers | FINS routing docs, node/network setup examples, HostLink serial audit | Create Omron docs set and make setup predictable for field users. |
| `Nexus.AllenBradley` | CIP, PCCC, virtual servers | Usable | 103 tests; CIP batch; fragmented tag APIs; string tags; CIP/PCCC servers | CIP tag syntax docs, PCCC/MicroLogix matrix, UDT/array scope audit, diagnostics docs | Create AllenBradley docs set and focus tag workflows. |

## Full Module Readiness

| Module | Status | Tests | Batch | Subscribe | Virtual/Server | Notes |
|--------|--------|-------|-------|-----------|----------------|-------|
| `Nexus.AllenBradley` | Usable | 103 | Yes | No | Yes | Top 5 track; CIP/PCCC need production gate evidence. |
| `Nexus.Bacnet` | Experimental | 40 | No | No | No | BACnet/IP APDU/BVLC tests exist; field validation and cleaner UDP base access needed. |
| `Nexus.Beckhoff` | Experimental | 4 | Yes | No | No | ADS client has batch signal but very thin tests. |
| `Nexus.Cjt` | Usable | 10 | No | No | No | Meter protocol; needs docs and serial validation matrix. |
| `Nexus.Delta` | Experimental | 10 | No | No | No | DVP client present; needs breadth audit and virtual test path. |
| `Nexus.Dlt` | Usable | 17 | No | No | No | DLT645/698 meter protocol; needs docs and device evidence. |
| `Nexus.Fanuc` | Experimental | 13 | No | No | No | FOCAS-style module; needs scope and real controller validation. |
| `Nexus.Fatek` | Usable | 11 | Yes | No | Yes | Batch and virtual server exist; tests should grow before production label. |
| `Nexus.Ftp` | Test Utility | 6 | No | No | No | Useful PLC file-transfer helper, not a core PLC memory protocol. |
| `Nexus.Fuji` | Experimental | 10 | No | No | Yes | Virtual server exists; protocol depth needs audit. |
| `Nexus.GeSrtp` | Experimental | 11 | No | No | Yes | Virtual server exists; needs GE device/address coverage. |
| `Nexus.Iec104` | Experimental | 22 | No | No | No | Energy/SCADA protocol; needs interoperability and long-run tests. |
| `Nexus.Inovance` | Usable | 34 | No | No | Yes | Better test signal than many B-tier modules; still needs product-family matrix. |
| `Nexus.Keyence` | Experimental | 4 | Yes | No | No | Batch exists but tests are too thin for promotion. |
| `Nexus.Kuka` | Experimental | 14 | No | No | No | Robot EKI client; needs robot protocol scope and integration validation. |
| `Nexus.LsElectric` | Needs Audit | 3 | No | No | No | Too little test evidence. |
| `Nexus.Mitsubishi` | Usable | 257 | Yes | No | Yes | Top 5 track; FX consolidated (编程口+计算机链接); MC3E ASCII/UDP frame tests added; HSL migration chapter done. |
| `Nexus.Modbus` | Production Candidate | 210 | Yes | Yes | Yes | Reference package candidate; FC08/22/43; offline packet parser; performance + long-run docs; shared WPF PacketRecorder. |
| `Nexus.Mqtt` | Usable | 30 | No | No | Server | MQTT client/broker utility; release separately from PLC protocol claims. |
| `Nexus.Omron` | Usable | 123 | Yes | No | Yes | Top 5 track; FINS/HostLink setup docs are critical. |
| `Nexus.OpcUa` | Experimental | 0 | No | Yes | No | Client has subscription signal but no matching tests found. |
| `Nexus.Panasonic` | Experimental | 3 | Yes | No | No | Batch exists but tests are too thin. |
| `Nexus.Redis` | Usable | 33 | No | No | No | Redis utility with connection pool; not PLC-specific. |
| `Nexus.Rkc` | Usable | 14 | No | No | No | Temperature-controller module; needs docs and device evidence. |
| `Nexus.Robot.Abb` | Experimental | 12 | No | No | No | Robot client; needs real-controller validation. |
| `Nexus.Robot.Efort` | Experimental | 11 | No | No | No | Robot client; needs scope docs and validation. |
| `Nexus.Robot.Estun` | Experimental | 6 | No | No | No | Modbus-backed robot workflow; tests are thin. |
| `Nexus.Robot.Fanuc` | Experimental | 10 | No | No | No | Robot socket client; needs controller validation. |
| `Nexus.Robot.Kuka` | Experimental | 16 | No | No | No | Two KUKA clients; needs consolidated story. |
| `Nexus.Robot.Yamaha` | Experimental | 7 | No | No | No | Needs controller validation and docs. |
| `Nexus.Robot.Yaskawa` | Experimental | 11 | No | No | No | Needs controller validation and docs. |
| `Nexus.Secs` | Experimental | 11 | No | No | No | HSMS/SECS module; needs interoperability tests. |
| `Nexus.Siemens` | Usable | 122 | Yes | No | Yes | Top 5 track; S7 is strong, PPI needs audit. |
| `Nexus.Toledo` | Usable | 10 | No | No | No | Scale protocol; needs docs and device evidence. |
| `Nexus.Xinje` | Experimental | 10 | No | No | No | Modbus-variant client; needs model coverage. |
| `Nexus.Yaskawa` | Usable | 44 | Yes | No | Yes | Memobus is relatively strong; needs docs and device matrix. |
| `Nexus.Yokogawa` | Usable | 52 | No | No | Yes | Stronger B-tier signal; needs field validation and docs. |

## Promotion Checklist

A module can move to `Production Candidate` only when the following are present:

1. Public address format documentation.
2. Supported device/model matrix.
3. At least one integration or virtual-server test path.
4. TX/RX event behavior documented and verified.
5. Error diagnostics documented for common failure modes.
6. Reconnect and timeout behavior documented.
7. Quickstart sample.
8. Real-device validation row, even if limited.

## Sprint 0 Work Queue

| Priority | Work Item | Output |
|----------|-----------|--------|
| P0 | Promote Modbus to reference package | `docs/protocols/modbus/*` created; complete scope added; `src/Nexus.Modbus/README.md` wired; offline packet parser; performance + long-run docs. |
| P0 | Define real-device validation template | `REAL_DEVICE_VALIDATION.md` created; 13 target rows for Modbus + Mitsubishi with detailed scratch address plans and L2/L4 checklists. |
| P0 | Define HSL migration guide outline | `HSL_MIGRATION_GUIDE.md` created; Modbus + Mitsubishi chapters complete. |
| P1 | Audit Siemens PPI and S7 docs gaps | Siemens docs skeleton and PPI audit created; focused SiemensPpi tests pass. |
| P1 | Audit Mitsubishi MC family parity | MC Binary/ASCII/UDP/A1E/FX consolidated into single package; 257 tests; support + complete-scope matrices; HSL migration chapter. |
| P1 | Fix WPF log viewer placeholder | Shared `PacketRecorderService`; all 7 Modbus pages use shared packet recording; JSONL export. |
| P1 | Create Omron docs set | Pending — `docs/protocols/omron/` does not exist yet. |
| P1 | Create AllenBradley docs set | Pending — `docs/protocols/allenbradley/` does not exist yet. |
| P2 | Add NuGet release checklist | `RELEASE_CHECKLIST.md` created; CI workflow with Modbus pack artifact. |

## Notes

- Test counts are from `dotnet test` execution as of 2026-06-09.
- `Nexus.MitsubishiFx` has been consolidated into `Nexus.Mitsubishi`; the separate project and test project are deleted.
- Interface signals are source scans. A protocol still needs feature-level audit before promotion.
- Real-device validation is intentionally a separate requirement; virtual servers are necessary but not sufficient.
