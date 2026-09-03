# SourceGen Auditor

[![Release](https://img.shields.io/github/v/release/dahornea/sourcegen-auditor?display_name=tag)](https://github.com/dahornea/sourcegen-auditor/releases/latest) [![License](https://img.shields.io/github/license/dahornea/sourcegen-auditor)](https://github.com/dahornea/sourcegen-auditor/blob/main/LICENSE)

> Audit what your incremental generator recomputes—and what Roslyn reuses.

SourceGen Auditor audits a compiled Roslyn incremental source generator across a controlled A → B → A scenario. It combines exact generated-output comparison with Roslyn tracked-step reasons, exposing cases where output stayed equal but the pipeline still recomputed.

Use it to:

- detect unstable output across fresh runs;
- verify relevant changes invalidate the registered output;
- verify irrelevant changes remain cached.

Unlike an ordinary generated-text snapshot test, the audit checks both what the generator produced and what Roslyn reported doing with the registered final output.

**[Download SourceGen Auditor v0.1.0](https://github.com/dahornea/sourcegen-auditor/releases/tag/v0.1.0)**

## Quick demo

Version 0.1.0 is verified on Windows x64 with PowerShell 7 and exactly .NET SDK 10.0.400. Every result is bounded to the selected generator, declared scenario, and recorded environment.

From PowerShell 7 at the repository root:

```powershell
dotnet restore ./SourceGenAuditor.slnx --locked-mode
dotnet build ./SourceGenAuditor.slnx --configuration Release --no-restore -warnaserror
dotnet run --project ./src/SourceGenAuditor.Cli/SourceGenAuditor.Cli.csproj `
  --configuration Release --no-build -- `
  audit ./tests/scenarios/irrelevant/scenario.json
```

The Release build materializes the two ignored demonstration manifests and their compiled fixture inputs. A passing irrelevant-change audit includes this abbreviated excerpt:

```text
SourceGen Auditor 0.1.0
Observed behavior under one declared controlled scenario.
Verdict: PASS
Partial evidence: false
Assertions:
[PASS] cold-output-determinism
[PASS] declared-source-effect
[PASS] declared-diagnostic-effect
[PASS] declared-invalidation
[PASS] restoration
[PASS] stable-restored-cache
... detailed evidence omitted ...
```

The full console report includes assertion reasons and detailed per-run evidence.

## What the audit checks

SourceGen Auditor runs two workers:

1. `coldA` executes A in a fresh process, with a fresh generator and driver.
2. `transitionA` executes the same A in a second fresh process.
3. `mutatedB` reuses that driver's returned state after applying the declared replacement.
4. `restoredA` reuses the next driver state after restoring the original input.
5. `stableA` runs unchanged restored A once more.

That sequence separates three questions:

- Did two fresh runs produce exactly equal generated sources and generator diagnostics?
- Did Roslyn report the registered final output as invalidated for a declared relevant change, or cached for a declared irrelevant change?
- Did restoring A restore its canonical output, and did the next unchanged A remain cached?

The manifest declares whether the mutation is relevant. The tool never infers the author's intent.

## Supported scope and requirements

Required:

- Windows x64;
- PowerShell 7 (`pwsh`);
- exactly .NET SDK 10.0.400, as pinned by `global.json`;
- a compiled C# generator assembly containing one selected `IIncrementalGenerator`;
- a manifest using the single supported source-text replacement scenario.

Version 0.1.0 supports one generator, one C# compilation, one explicit metadata reference, console and JSON reports, and the pinned Roslyn 5.9.0 host/admission policy. It does not load projects or solutions, infer mutation relevance, exercise multiple generators or languages, benchmark performance, or establish behavior in Visual Studio or `dotnet build`.

## Install version 0.1.0

The package is not published to NuGet.org. Download `SourceGenAuditor.Tool.0.1.0.nupkg` from the [v0.1.0 release](https://github.com/dahornea/sourcegen-auditor/releases/tag/v0.1.0), or use the [direct package download](https://github.com/dahornea/sourcegen-auditor/releases/download/v0.1.0/SourceGenAuditor.Tool.0.1.0.nupkg).

Expected SHA-256:

```text
4feb853bfdcfd4db21f21fbf35385588fc0cd8aacc7674dfebb700a4d08cbbfe
```

From PowerShell in the directory containing the downloaded package, verify and install it as a local tool source:

```powershell
(Get-FileHash ./SourceGenAuditor.Tool.0.1.0.nupkg -Algorithm SHA256).Hash.ToLowerInvariant()

dotnet tool install SourceGenAuditor.Tool `
  --version 0.1.0 `
  --tool-path ./.tools `
  --add-source .

& ./.tools/sourcegen-auditor.exe --help
```

Uninstall from the same tool path:

```powershell
dotnet tool uninstall SourceGenAuditor.Tool --tool-path ./.tools
```

<details>
<summary>Build and install from source</summary>

### Local-only source build

`SourceGenAuditor.Tool` 0.1.0 is not published to NuGet.org. Build and install it only from the package produced by this repository:

```powershell
dotnet restore ./SourceGenAuditor.slnx --locked-mode
dotnet build ./SourceGenAuditor.slnx --configuration Release --no-restore -warnaserror
dotnet pack ./src/SourceGenAuditor.Cli/SourceGenAuditor.Cli.csproj `
  --configuration Release --no-build --output ./artifacts/packages

$packageDirectory = (Resolve-Path ./artifacts/packages).Path
$nugetConfig = Join-Path $PWD 'artifacts/local-only.NuGet.Config'
$escapedPackageDirectory = [Security.SecurityElement]::Escape($packageDirectory)
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="sourcegen-auditor-local" value="$escapedPackageDirectory" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $nugetConfig -Encoding utf8NoBOM

dotnet tool install SourceGenAuditor.Tool --version 0.1.0 `
  --tool-path ./artifacts/tool --configfile $nugetConfig --no-cache
```

The `<clear />` entry makes this a local-only package source rather than a fallback to NuGet.org.

Check the installed command and run either included example scenario:

```powershell
& ./artifacts/tool/sourcegen-auditor.exe --help
& ./artifacts/tool/sourcegen-auditor.exe audit --help
& ./artifacts/tool/sourcegen-auditor.exe audit `
  ./tests/scenarios/relevant/scenario.json
& ./artifacts/tool/sourcegen-auditor.exe audit `
  ./tests/scenarios/irrelevant/scenario.json
```

Uninstall from the same tool path:

```powershell
dotnet tool uninstall SourceGenAuditor.Tool --tool-path ./artifacts/tool
```

</details>

## Scenario manifest

A scenario identifies the generator assembly and exact type, compiler settings, baseline source and hashes, one replacement mutation, and expectations for generated sources and diagnostics. The mutation's `relevance` is explicitly `relevant` or `irrelevant`.

Paths are resolved beneath the manifest directory, validated before worker launch, and redacted in reports. See the complete [scenario and report contract](https://github.com/dahornea/sourcegen-auditor/blob/main/docs/scenarios.md) before authoring a manifest.

## Console and JSON reports

Console is the default format. The summary appears before the full compatibility, canonical snapshot, diagnostic, tracked-step, environment, and worker-stream evidence:

```powershell
& ./artifacts/tool/sourcegen-auditor.exe audit ./scenario.json
```

Use JSON for automation. Without `--output`, the report is the only content written to stdout; operational messages go to stderr. With `--output`, the report is written atomically to that path:

```powershell
& ./artifacts/tool/sourcegen-auditor.exe audit ./scenario.json --format json
& ./artifacts/tool/sourcegen-auditor.exe audit ./scenario.json --format json --output ./artifacts/audit.json
```

The JSON schema remains versioned as V1. Reports contain canonical hashes and comparison evidence, not full generated-source text.

## Verdicts and exit codes

| Exit | Result | Meaning |
| ---: | --- | --- |
| `0` | `PASS` | Every required assertion passed with complete evidence. |
| `1` | `FAIL` | Complete evidence contradicted at least one declared expectation. |
| `2` | `UNKNOWN` | Required public evidence was unavailable, so the tool could not claim pass or fail. |
| `3` | `ERROR` | An operational or evidence failure occurred, such as load, worker, protocol, timeout, or report-write failure. |
| `64` | — | The command line or scenario was invalid. |
| `130` | `ERROR` | The user canceled; cleanup completed and best-effort evidence was retained. |

`PASS` is not a global proof of determinism, semantic correctness, purity, compatibility, or optimal incremental caching. It describes only the observed behavior under the declared scenario and recorded environment.

## Security boundary

The selected generator executes arbitrary code with the user's permissions and can access the same local resources. The worker process contains hangs and crashes at a process boundary; it is not a security sandbox. Do not run malicious or untrusted generators.

## Development and verification

Run the test suite with Microsoft Testing Platform v2:

```powershell
dotnet restore ./SourceGenAuditor.slnx --locked-mode
dotnet build ./SourceGenAuditor.slnx --configuration Release --no-restore -warnaserror
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
dotnet test --solution ./SourceGenAuditor.slnx --configuration Release --no-build
```

Run the repository-shape and contract verifier separately:

```powershell
pwsh -NoProfile -File ./eng/verify.ps1
```

The Phase 0 verifier checks repository shape and normative documentation vectors; it does not execute a generator or replace product acceptance. The complete reproducible package acceptance sequence is preserved in the [delivery plan](https://github.com/dahornea/sourcegen-auditor/blob/main/PLAN.md).

## Engineering records

- [Product specification](https://github.com/dahornea/sourcegen-auditor/blob/main/PRODUCT.md)
- [Architecture and evidence contracts](https://github.com/dahornea/sourcegen-auditor/blob/main/ARCHITECTURE.md)
- [Scenario and report contracts](https://github.com/dahornea/sourcegen-auditor/blob/main/docs/scenarios.md)
- [Architecture decision records](https://github.com/dahornea/sourcegen-auditor/blob/main/docs/decisions/README.md)
- [Delivery and verification plan](https://github.com/dahornea/sourcegen-auditor/blob/main/PLAN.md)
- [Read-only review record](https://github.com/dahornea/sourcegen-auditor/blob/main/CODE_REVIEW.md)
- [MIT License](https://github.com/dahornea/sourcegen-auditor/blob/main/LICENSE)
- [Third-party notices](https://github.com/dahornea/sourcegen-auditor/blob/main/THIRD-PARTY-NOTICES.md)
