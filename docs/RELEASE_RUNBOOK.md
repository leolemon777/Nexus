# Nexus App Release Runbook

Operational guide for cutting a release of the **Nexus WPF debugger** (`src/Nexus.App`).
Scope: ship a Windows x64 self-contained single-file `.exe` with **no external cert** and
**no installer binary** required. Code signing, installer, and auto-update are deliberately
deferred (see [Deferred decisions](#deferred-decisions)).

> Target: .NET 8 (`net8.0-windows`), WPF, `UseWPF=true`, `AssemblyName=Nexus`.

---

## 1. Versioning

The whole solution uses **MinVer** (git-tag-driven versioning), configured in
`Directory.Build.props` at the repo root:

```xml
<VersionPrefix>1.0.0</VersionPrefix>
<MinVerTagPrefix>v</MinVerTagPrefix>
<PackageReference Include="MinVer" Version="5.0.0" PrivateAssets="All" />
```

### How versions are produced

- **With a git tag** `vMAJOR.MINOR.PATCH` (e.g. `v1.0.0`, `v1.2.0-beta.3`): MinVer sets the
  assembly/package version to exactly that tag. Commits after the tag get auto-incremented
  prerelease metadata.
- **With no git tag** (current state of this repo as of 2026-06-19): MinVer falls back to
  `VersionPrefix` (1.0.0) plus commit-count prerelease metadata, e.g. `1.0.0-alpha.42`.
- **If any csproj hardcodes `<Version>`**: that value overrides MinVer for that project only
  (this is the trap that previously affected `Nexus.App`, which was pinned to `0.1.0`).

### Decision applied in this release (WS-E)

**Removed the hardcoded `<Version>0.1.0</Version>` from `src/Nexus.App/Nexus.App.csproj`**
so MinVer applies to the app the same way it applies to the rest of the solution.

Evidence gathered before editing:

| File | Before | After |
|---|---|---|
| `Directory.Build.props` | MinVer wired (`VersionPrefix=1.0.0`, `MinVerTagPrefix=v`, `PackageReference MinVer 5.0.0`) | unchanged (added clarifying comment only) |
| `src/Nexus.App/Nexus.App.csproj` | `<Version>0.1.0</Version>` (overrode MinVer) | `<Version>` removed — MinVer now drives it |
| Every other `src/*.csproj` | hardcoded `<Version>1.0.0</Version>` (overrides MinVer) | **NOT touched** (out of scope for WS-E; see [Known packaging inconsistencies](#known-packaging-inconsistencies)) |

A literal `<Version>` was **not** added to `Directory.Build.props` because MinVer is genuinely
wired (the `PackageReference` is present) — adding a central `<Version>` would have overridden
MinVer everywhere, the opposite of the goal.

### Cutting a versioned release

```bash
# 1. Decide the version (SemVer). First stable release example:
git tag v1.0.0
git push origin v1.0.0          # if/when a remote exists — see "Repo state" below

# 2. MinVer will now emit 1.0.0 for every project that does NOT override <Version>.
#    Verify the app version:
dotnet build src/Nexus.App/Nexus.App.csproj -v:n
# Inspect obj/Nexus.App.csproj.nuget.g.props for MinVer-set <Version> / <PackageVersion>.
```

### Repo state caveat (2026-06-19)

- **No git remote is configured** (`git remote -v` is empty). Tag pushing and remote-based
  MinVer resolution will require a remote to be added first. Local tags work for local builds.
- **No git tags exist yet.** The first `v1.0.0` tag is the user's decision and not created here.

---

## 2. Build

```bash
# Full solution build (sanity check; uses Nexus.slnx, NOT .sln)
dotnet build Nexus.slnx -c Release

# App only
dotnet build src/Nexus.App/Nexus.App.csproj -c Release
```

`dotnet build` / `dotnet run` are **RID-agnostic** — `RuntimeIdentifier=win-x64` lives only
inside the publish profile, so default builds are not pinned to a single RID.

---

## 3. Publish (the actual artifact)

```bash
# Single-file, self-contained, ReadyToRun, win-x64, no signing, no installer.
dotnet publish src/Nexus.App/Nexus.App.csproj ^
  -c Release ^
  -p:PublishProfile=Properties/PublishProfiles/win-x64.pubxml
```

Output lands in:

```
src/Nexus.App/bin/Release/net8.0-windows/win-x64/publish/
```

and is a single `Nexus.exe` (native libraries bundled via `IncludeNativeLibrariesForSelfExtract`).
It runs on a clean Windows x64 machine with **no .NET runtime pre-installed**.

Profile location: `src/Nexus.App/Properties/PublishProfiles/win-x64.pubxml`.

Properties applied (Release-only, from csproj + pubxml):

| Property | Value | Where |
|---|---|---|
| `RuntimeIdentifier` | `win-x64` | pubxml only |
| `SelfContained` | `true` | pubxml (`ForceSelfContained=true` also in csproj) |
| `PublishSingleFile` | `true` | csproj (Release) + pubxml |
| `PublishReadyToRun` | `true` | csproj (Release) + pubxml |
| `IncludeNativeLibrariesForSelfExtract` | `true` | csproj (Release) + pubxml |
| `DebugType` | `none` (Release) | csproj (Release) + pubxml |
| `SignAssembly` | `false` (deferred) | csproj |

### Verifying the artifact

- Right-click the produced `Nexus.exe` → Properties → Details: confirm the **File version**
  / **Product version** matches the MinVer-produced version (not `0.1.0`).
- Launch on a clean Windows x64 VM without .NET 8 installed: should start without a runtime
  prompt.
- Smoke test: connect to a Modbus TCP virtual server (`Nexus.Modbus.VirtualServer`) from the
  Modbus page to confirm the WPF shell, DI, and a representative protocol all load.

---

## 4. Environment matrix

| Component | Requirement |
|---|---|
| Build OS | Windows 10/11 x64 (WPF + `net8.0-windows` target) |
| .NET SDK | .NET 8 SDK (`net8.0-windows` target framework) |
| Target OS | Windows x64 (single-file is RID-locked to `win-x64` by the profile) |
| Runtime on target | **None required** (self-contained) |
| Disk on target | ~150–250 MB extracted (ReadyToRun + self-contained); runs from a single .exe |

Cross-RID (e.g. `win-x86`, `linux`, `macos`) is **not** supported by this profile and is out
of scope — WPF + `net8.0-windows` is Windows-only by definition.

---

## 5. Rollback

There is no installer / auto-update pipeline yet (deferred). Rollback is manual:

1. Replace `Nexus.exe` on the target machine with the previous known-good build.
2. If app-local settings/state exist alongside the exe (`appsettings.json`, SQLite stores),
  preserve them — they are copied to output via `CopyToOutputDirectory=PreserveNewest`.

Because there is no installer, there is no "uninstall" step — deleting the folder is the full
rollback. Recommend keeping the prior `.exe` until the new one is validated.

---

## 6. Monitoring

The app currently has **no telemetry or crash-reporting pipeline** wired into the release
build. For a first release, recommended minimal observability (flagged, not implemented):

- Confirm `ILogger` / `IMessageLogger` paths still emit to the in-app log (500-line FIFO).
- If a crash dialog appears on launch, capture the Windows event log entry under
  `Application Logs → Nexus` before replacing the binary.

A real crash/log uploader is deferred (see [Deferred decisions](#deferred-decisions)).

---

## 7. Known packaging inconsistencies

Items 1 and 2 below were **resolved** in a follow-up cleanup pass (the same pass that
landed the WS-E app versioning fix). Items 3 and 4 remain open.

1. **RESOLVED — MinVer is now authoritative solution-wide.** All 52 library csprojs
   previously hardcoded `<Version>1.0.0</Version>` (plus duplicated
   `<Authors>`/`<Company>`/`<PackageLicenseExpression>`/`<RepositoryUrl>`/
   `<RepositoryType>`/`<PackageRequireLicenseAcceptance>`), which silently overrode MinVer.
   Those duplicate metadata lines were removed so `Directory.Build.props` is the single
   source of truth. **Verified:** with no git tag, `Nexus.Modbus.dll` now builds to
   `ProductVersion = 0.0.0-alpha.0.68+9a20fece1e...` (MinVer commit-count prerelease)
   instead of a flat `1.0.0`. Cutting `git tag v1.0.0` will now produce `1.0.0` for every
   package, not just the app. Per-project `<Description>`/`<PackageTags>`/
   `<PackageReadmeFile>`/`<AllowUnsafeBlocks>` are intentionally kept (project-specific).
2. **RESOLVED — Repo URL unified.** `Directory.Build.props` (`PackageProjectUrl` +
   `RepositoryUrl`) is the canonical source: `https://github.com/nexus-iot/nexus`.
   The per-project `nexus-industrial` `RepositoryUrl` lines were removed as part of the
   cleanup in item 1. **Verified:** `grep -r nexus-industrial src --include=*.csproj`
   (excluding `obj/`) returns 0 matches. (Stale `obj/` build artifacts still reference the
   old URL; they regenerate on the next clean build.)
3. **OPEN — No git remote configured.** `git remote -v` is empty. MinVer resolves versions
   from the local tag graph, but pushing tags / SourceLink requiring a published repo URL
   will need a remote added first.
4. **OPEN — `app.manifest` still pins `assemblyIdentity version="1.0.0.0"`** (COM/Shell
   version resource, unrelated to .NET assembly version). Harmless for now; revisit if
   Windows "Programs and Features" / file-properties version display needs to track
   releases.

---

## 8. Deferred decisions (require user input — do NOT implement without approval)

### A. Code signing
- **Status:** `SignAssembly=false` in `Nexus.App.csproj`. No `.pfx`/`.snk`/cert referenced.
- **Blocker:** requires an OV or EV code-signing cert the user must procure (cost, identity
  verification lead time).
- **To enable (future):** procure cert → store out-of-repo → set `SignAssembly=true`,
  `AssemblyOriginatorKeyFile` (or use `signtool` / Azure Trusted Signing) → add the runbook
  step. **Do not commit the cert to the repo.**
- **Risk of shipping unsigned:** Windows SmartScreen / Defender may flag the unsigned
  single-file exe as unrecognized on first run. Users can bypass via "More info → Run anyway".
  This is the expected trade-off for a cert-free first release.

### B. Installer
- **Status:** none. The release is a raw single-file `.exe`.
- **Recommended option:** **Velopack** (modern, delta updates, single-file friendly). Alternatives:
  MSI (WiX v4), Inno Setup.
- **Blocker:** adding Velopack/Squirrel/WiX is a NuGet add + build-pipeline change. Per the
  WS-E hard rules, **Velopack/Squirrel NuGet must NOT be added without explicit approval.**
- **Decision needed from user:** which installer technology, and whether per-machine
  (`%ProgramFiles%`) or per-user (`%LocalAppData%`) install scope.

### C. Auto-update
- **Status:** none.
- **Dependencies:** depends on the installer choice (Velopack bundles its own update
  mechanism; MSI would need Squirrel/Clowd or a custom manifest) **and** on hosting an update
  manifest (static JSON on GitHub Releases, S3, or a CDN).
- **Decision needed from user:** installer choice (A above), manifest host, and update channel
  strategy (stable vs. pre-release). Do not implement until A and B are decided.

---

## 9. Release checklist (WS-E scope)

- [ ] Confirm `git status` clean on the files WS-E owns (csproj, props, pubxml, this runbook).
- [ ] `dotnet build Nexus.slnx -c Release` (orchestrator runs this after concurrent agents
      land — do NOT run it inside WS-E due to Windows obj/ lock contention with peer agents).
- [ ] `dotnet publish src/Nexus.App/Nexus.App.csproj -c Release -p:PublishProfile=Properties/PublishProfiles/win-x64.pubxml`
- [ ] Verify produced `Nexus.exe` version matches MinVer output (not `0.1.0`).
- [ ] Smoke test on a clean Windows x64 machine (no .NET 8 installed).
- [ ] (Optional, user decision) cut `v1.0.0` git tag for a clean version string.
- [ ] Capture any SmartScreen warning text for the release notes (expected: unsigned exe).

---

## 10. Files touched by WS-E

| File | Action |
|---|---|
| `src/Nexus.App/Nexus.App.csproj` | edited — removed hardcoded `<Version>0.1.0</Version>`, added Release publish PropertyGroup, added `<SignAssembly>false</SignAssembly>` placeholder |
| `Directory.Build.props` | edited — additive comment only (MinVer settings unchanged) |
| `src/Nexus.App/Properties/PublishProfiles/win-x64.pubxml` | created — self-contained single-file profile, no signing |
| `docs/RELEASE_RUNBOOK.md` | created — this document |
