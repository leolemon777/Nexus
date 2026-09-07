# Nexus Build Evidence

- Overall: PASS
- Generated at: 2026-09-07T16:17:05.624Z
- Qualification: candidate-clean-source
- Source commit: 2b42c3f376fe44c5e29fc171a134e5b8b8f58476
- Source dirty: false
- Release manifest: release-manifest.json

## Locked Node, npm, Electron, and Rust toolchain

- Command:
  ```text
powershell.exe -NoProfile -NonInteractive -Command npm.cmd run preflight:toolchain
  ```
- Exit: 0
- Duration: 1073 ms
- Log: toolchain-preflight.log
- Log SHA-256: 7a0ca55007abdf6679563658809f2bba79ad81bf44ca4099eeddb16660c75c76

## Rust JSONL, protocol interop, and R0 soak harness

- Command:
  ```text
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File E:\Desktop\Nexus2.0\nexus-rust\scripts\test-rust-jsonl.ps1
  ```
- Exit: 0
- Duration: 15071 ms
- Log: rust-jsonl-and-interop.log
- Log SHA-256: 3dbf34baa06324eeef81b2d95a1a40c55f98808adbbc1b781b67cd13bd22a3b8

## Electron and release tooling regression

- Command:
  ```text
powershell.exe -NoProfile -NonInteractive -Command npm.cmd run test:electron
  ```
- Exit: 0
- Duration: 24021 ms
- Log: electron-and-release-regression.log
- Log SHA-256: 8029a771a89873ead73e0f80555509b5ab56a8cafa0985736568e25cefa865ab

## Vite production build

- Command:
  ```text
powershell.exe -NoProfile -NonInteractive -Command npm.cmd run build
  ```
- Exit: 0
- Duration: 1293 ms
- Log: web-build.log
- Log SHA-256: 1e3b9280aa2b402b27a5c12907d53d1c2cb4d0441a383e73fd4e525457f3d09d

## Electron desktop smoke

- Command:
  ```text
powershell.exe -NoProfile -NonInteractive -Command npm.cmd run smoke:electron
  ```
- Exit: 0
- Duration: 1035 ms
- Log: electron-smoke.log
- Log SHA-256: 68da8e6a11a2a8a231f6dc74930e3839866fe2f8e22d40c0f959f4b880d9d01d

## npm security audit

- Command:
  ```text
powershell.exe -NoProfile -NonInteractive -Command npm.cmd run audit
  ```
- Exit: 0
- Duration: 4129 ms
- Log: npm-audit.log
- Log SHA-256: 38118156b2a46d0a2b90636115b749c2107d09fce2f70bbe1b0bd1414c0a33e4

