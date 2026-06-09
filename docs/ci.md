# Continuous Integration

The default GitHub Actions workflow is `.github/workflows/build-test.yml`.

## Runner

The workflow uses `windows-latest` because the solution includes the WPF debugger app targeting `net8.0-windows`. The protocol libraries target `netstandard2.0`.

The workflow installs both .NET 8 and .NET 10 SDKs. .NET 8 supplies the target runtime/tooling surface for the WPF app, while the current repository uses `Nexus.slnx`, which is handled by the newer SDK installed on the runner.

## Commands

```powershell
dotnet restore Nexus.slnx
dotnet build Nexus.slnx --configuration Release --no-restore
dotnet test Nexus.slnx --configuration Release --no-build -- xunit.parallelizeTestCollections=false
dotnet pack src\Nexus.Modbus --configuration Release --no-build --output artifacts\packages
```

The workflow currently disables xUnit collection parallelization as a conservative default while all protocol integration tests are audited. `tests\Nexus.Modbus.Tests` now passes with the normal parallel-capable test command after its UDP server tests were moved to dynamic ports.

## Packaging

The workflow packs `Nexus.Modbus` as the first release-candidate package and uploads the `.nupkg` as a workflow artifact. It intentionally does not publish to NuGet and does not require any NuGet API token or release secret.
