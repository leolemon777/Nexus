# Third-party notices

## Reverie design system

Nexus 2.0 uses and adapts the Reverie design-system package supplied under the **Uiverse Design System License v1**.

Copyright © 2026 Uiverse. All rights reserved.

The source package is kept at `../reverie`. Its complete license is `../reverie/LICENSE.md`. The license permits commercial, personal, and client-project use and modification, but forbids resale, republication, redistribution of the ZIP, marketplace upload, repackaging as a competing template, or redistribution of its source assets as a competing component library.

When Nexus 2.0 is packaged for distribution, this notice and the complete Reverie license must be included with the application.

## Fonts and icons

Reverie specifies Literata and Mona Sans through Google Fonts and uses Phosphor Icons under the MIT license. Their own licenses remain applicable. Production packaging will vendor required font files so the industrial desktop application does not depend on internet access.

## npm production dependencies

Nexus 2.0 uses the npm production dependency **serialport 13.0.0** and the direct runtime package **@tauri-apps/api 2.x**. Serialport and its scoped runtime packages are distributed under MIT or BSD-2-Clause as recorded in `package-lock.json`; Tauri's API package records an MIT or Apache-2.0 dual license. Complete name, version, integrity, and SPDX license expressions are enumerated in `evidence/supply-chain/license-report.json`.

## Rust production dependency closure

The Rust sidecar directly depends on **serde**, **serde_json**, and **thiserror**. Their registry crates and transitive dependencies use permissive SPDX expressions such as MIT OR Apache-2.0, Unlicense OR MIT, and Unicode-3.0 combinations. The complete Cargo name/version/license inventory is generated in `evidence/supply-chain/license-report.json`; Cargo.lock remains the authoritative version and checksum source.

## MQTT interoperability test dependencies

The development and interoperability test suite uses **Aedes 1.1.1** as an independent MQTT 3.1.1 broker and **MQTT.js 5.15.2** as an independent publishing client. Both projects are distributed under the MIT license.

These packages are development-only dependencies used by `scripts/mqtt-aedes-integration.test.cjs`; they are not part of the Nexus production MQTT runtime or the packaged Rust sidecar. Their transitive dependencies retain their respective licenses as recorded in `package-lock.json`.

## BACnet interoperability test dependency

The development and interoperability test suite uses **bacstack 0.0.1-beta.14** as an independent MIT-licensed BACnet/IP stack.

This package is a development-only dependency used by `scripts/bacnet-bacstack-integration.test.cjs`; it is not part of the Nexus production BACnet runtime or the packaged Rust sidecar. Its transitive dependencies retain their respective licenses as recorded in `package-lock.json`.

## KNX interoperability test dependency

The development and interoperability test suite uses **knx 2.5.4** as an independent MIT-licensed KNXnet/IP protocol implementation. Its parser and encoder are exercised directly by `scripts/knx-independent-stack-integration.test.cjs`; the package's full client state machine is not part of the Nexus production runtime.

This package is a development-only dependency and is not packaged with the Rust sidecar. An npm override pins its transitive `binary-parser` dependency to `^2.3.0` so `npm audit` remains clean. All transitive licenses remain recorded in `package-lock.json`.
