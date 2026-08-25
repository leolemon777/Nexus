# Nexus Build Evidence

- Overall: PASS
- Generated at: 2026-08-23T12:53:36.791Z
- Qualification: candidate-dirty-source
- Source commit: 93cd0ee270db81b1a1cbe27d43022dd99a9b4a3d
- Source dirty: true
- Release manifest: not supplied

## Rust JSONL, protocol interop, and R0 soak harness

- Command:
  ```text
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File E:\Desktop\Nexus2.0\Nexus-Rust\scripts\test-rust-jsonl.ps1
  ```
- Exit: 0
- Duration: 8475 ms
- Log: rust-jsonl-and-interop.log
- Log SHA-256: 3fc825592005be3472f7fc4b322b5fc3c5112d3cc64e462a72b2fbbe8e5bf1cb

## Electron and release tooling regression

- Command:
  ```text
powershell.exe -NoProfile -NonInteractive -Command npm.cmd run test:electron
  ```
- Exit: 0
- Duration: 1836 ms
- Log: electron-and-release-regression.log
- Log SHA-256: 4bdb68a81947875e422ee667c80ca47e02d7ea7196c21e3371cff5f2d10c5aff

## Vite production build

- Command:
  ```text
powershell.exe -NoProfile -NonInteractive -Command npm.cmd run build
  ```
- Exit: 0
- Duration: 1270 ms
- Log: web-build.log
- Log SHA-256: 7950f5d1c53c3afc6d69179f758a3dfbd185cfd1d1755aeaf7f41360ea1810ab

## Electron desktop smoke

- Command:
  ```text
powershell.exe -NoProfile -NonInteractive -Command npm.cmd run smoke:electron
  ```
- Exit: 0
- Duration: 1143 ms
- Log: electron-smoke.log
- Log SHA-256: 79ee05c40b838485a8e3e16d6f3335c2f713fe85efdc73c9ccd7bd6c18527cfd

## npm security audit

- Command:
  ```text
powershell.exe -NoProfile -NonInteractive -Command npm.cmd run audit
  ```
- Exit: 0
- Duration: 2691 ms
- Log: npm-audit.log
- Log SHA-256: 71a252cbef2da018dad6efd8d83ac0e99d9e3240679955d617c0e61429ebac1c

