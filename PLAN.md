# Delivery plan

## Historical gate state and current status

The Architecture Gate and amendments F5-01/F5-02 are approved, and Phase 1 was completed at commit `ef11d3167b51ed1fb87514115c93102b43c9234c`. This plan remains the reproducible historical record of the implementation and acceptance sequence. Phase 2 is not approved.

## Locked project configuration

Phase 1 uses one test engine: Microsoft Testing Platform v2. The checked-in `global.json` is exactly:

```json
{
  "sdk": {
    "version": "10.0.400",
    "rollForward": "disable",
    "allowPrerelease": false
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

`SourceGenAuditor.Tests.csproj` targets `net10.0` and locks:

```xml
<OutputType>Exe</OutputType>
<IsTestProject>true</IsTestProject>
<IsPackable>false</IsPackable>
<PackageReference Include="xunit.v3.mtp-v2" />
```

Central package management pins `xunit.v3.mtp-v2` to 4.0.0. Do not reference `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio`, and do not set `TestingPlatformDotnetTestSupport`. The test project is the only executable test application. `SourceGenAuditor.Fixtures` contains exactly one configurable `IIncrementalGenerator` fixture and is not a test project.

F1's sentinel is one xUnit `[Fact]` whose explicit display name is exactly `SourceGenAuditor.Tests.Infrastructure.MtpContractTests.XunitMtpV2ProducesTrx`; it must pass and appear under that exact `testName` in the xUnit TRX.

`Directory.Packages.props` enables central package management and package lock files, and its direct package-version entries are exactly:

```xml
<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
<PackageVersion Include="Microsoft.CodeAnalysis.CSharp" Version="5.9.0" />
<PackageVersion Include="xunit.v3.mtp-v2" Version="4.0.0" />
```

The initial restore creates lock files; they are reviewed and committed before F1. Every subsequent restore, including the acceptance sequence below, uses `--locked-mode`.

The CLI project locks:

```xml
<OutputType>Exe</OutputType>
<PackAsTool>true</PackAsTool>
<ToolCommandName>sourcegen-auditor</ToolCommandName>
<PackageId>SourceGenAuditor.Tool</PackageId>
<Version>0.1.0</Version>
```

The deliverable is a framework-dependent installable .NET tool. NuGet.org publishing is deferred.

## Historical exact Phase 1 implementation sequence

The completed Phase 1 sequence was:

1. Add the pinned `global.json`, solution, two production projects, one executable MTP v2 test project, one single-generator fixture project, central package versions, and lock files exactly as specified above.
2. Run falsification gate F1: locked restore, build, MTP execution, positive test counts, and a passing named xUnit/MTP sentinel in the TRX. Stop and reopen the gate if it fails.
3. Implement the dependency-free scenario, evidence, failure, assertion, verdict, and report V1 domain types.
4. Implement strict scenario validation, path containment/redaction, a parent-held input lease across both workers, immutable byte snapshots, file hashes, and recursive metadata-only dependency closure inspection for every host-unified Roslyn assembly reference.
5. Implement the locked source and diagnostic canonicalizers plus published known vectors; run F5 and stop/re-gate on any byte or hash disagreement.
6. Implement the one configurable generator fixture and Roslyn adapter with tracking, fresh cold runs, immutable driver reuse, partial results, and no opaque-value serialization; run F2 and F3. F2 failure is `NO-GO`.
7. Implement exact named-type loading, host Roslyn unification, lower-or-equal admission, newer rejection, fixture-coverage reporting, and typed failures. Do not advertise lower-version compatibility.
8. Implement the two-pipe protocol, frame/schema/sequence validation, byte limits, checkpoint deadlines, stdout/stderr draining, cancellation, process-tree cleanup, atomic report files, and partial-evidence retention; run F4 and F7 and stop/re-gate on failure.
9. Implement the six assertions, stable reason codes, evidence links, aggregate precedence, report mapping, deterministic JSON, console rendering, and exit mapping.
10. Implement the public CLI and hidden worker entry point without a CLI framework; keep worker invocation valid from an installed tool shim.
11. Add the complete unit, adapter, worker, CLI, failure, protocol, and end-to-end matrix using only the one generator fixture.
12. Pack `SourceGenAuditor.Tool` 0.1.0, install it from the local package output to an isolated tool path, execute version and both scenario audits through the installed shim, uninstall it, and run F6.
13. Generate the resolved direct/transitive license inventory and update documentation only with behavior demonstrated by the exact verification sequence.

Any failed F1-F7 condition has the consequence stated in `ARCHITECTURE.md`. Do not substitute a runner, dependency, compatibility claim, IPC, limit, process topology, hash format, report schema, or distribution form without a new Architecture Gate.

## Exact Phase 1 verification sequence

Run in PowerShell 7 from the repository root on Windows. A prior result never substitutes for this fresh, complete Phase 1 acceptance sequence.

```powershell
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if (-not (Test-Path -LiteralPath './SourceGenAuditor.slnx' -PathType Leaf)) { throw 'Run this sequence from the repository root.' }

$packageVersion = '0.1.0'
$acceptanceRunId = [Guid]::NewGuid().ToString('N')
$acceptancePath = Join-Path (Join-Path $PWD 'artifacts') "acceptance-$acceptanceRunId"
$testResultsPath = Join-Path $acceptancePath 'TestResults'
$packagesPath = Join-Path $acceptancePath 'packages'
$toolPath = Join-Path $acceptancePath 'tool-smoke'
$nugetPackagesPath = Join-Path $acceptancePath 'nuget-packages'
$nugetHttpCachePath = Join-Path $acceptancePath 'nuget-http-cache'
$nugetPluginsCachePath = Join-Path $acceptancePath 'nuget-plugins-cache'
$dotnetCliHomePath = Join-Path $acceptancePath 'dotnet-home'
$nugetConfigPath = Join-Path $acceptancePath 'NuGet.Config'
$relevantReportPath = Join-Path $acceptancePath 'relevant.json'
$irrelevantReportPath = Join-Path $acceptancePath 'irrelevant.json'
$trxPath = Join-Path $testResultsPath 'SourceGenAuditor.Tests.trx'

pwsh -NoProfile -File ./eng/verify.ps1

$sdkVersion = (& dotnet --version).Trim()
if ($sdkVersion -ne '10.0.400') { throw "Expected SDK 10.0.400, got $sdkVersion." }

$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
dotnet restore ./SourceGenAuditor.slnx --locked-mode
dotnet build ./SourceGenAuditor.slnx --configuration Release --no-restore -warnaserror

New-Item -ItemType Directory -Path $testResultsPath, $packagesPath | Out-Null

dotnet test --solution ./SourceGenAuditor.slnx --configuration Release --no-build --results-directory $testResultsPath --report-xunit-trx --report-xunit-trx-filename SourceGenAuditor.Tests.trx
if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) { throw 'TRX report was not created.' }
$trx = [xml](Get-Content -LiteralPath $trxPath -Raw)
if ($trx.DocumentElement.LocalName -ne 'TestRun' -or $trx.DocumentElement.NamespaceURI -ne 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010') { throw 'Report is not a recognized TRX TestRun.' }
$counters = $trx.SelectSingleNode("//*[local-name()='ResultSummary']/*[local-name()='Counters']")
if ($null -eq $counters -or [int]($counters.GetAttribute('total')) -le 0 -or [int]($counters.GetAttribute('executed')) -le 0) { throw 'TRX contains no executed tests.' }
$sentinelName = 'SourceGenAuditor.Tests.Infrastructure.MtpContractTests.XunitMtpV2ProducesTrx'
$sentinel = $trx.SelectSingleNode("//*[local-name()='UnitTestResult'][@testName='$sentinelName' and @outcome='Passed']")
if ($null -eq $sentinel) { throw 'Passing xUnit/MTP TRX sentinel result was not found.' }

dotnet pack ./src/SourceGenAuditor.Cli/SourceGenAuditor.Cli.csproj --configuration Release --no-build --output $packagesPath -p:PackageVersion=$packageVersion
$packagePath = Join-Path $packagesPath "SourceGenAuditor.Tool.$packageVersion.nupkg"
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw 'Tool package was not created.' }
$packageSha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$packageSha256 | Set-Content -LiteralPath (Join-Path $acceptancePath 'package.sha256') -Encoding ascii -NoNewline
$packageEntries = tar -tf $packagePath
foreach ($requiredPackageEntry in @('README.md', 'THIRD-PARTY-NOTICES.md')) {
    if ($packageEntries -notcontains $requiredPackageEntry) { throw "Tool package is missing $requiredPackageEntry." }
}
$noticeText = (tar -xOf $packagePath 'THIRD-PARTY-NOTICES.md') -join "`n"
if ($noticeText -notmatch 'Copyright \(c\) \.NET Foundation and Contributors' -or
    $noticeText -notmatch 'Permission is hereby granted') { throw 'Tool package is missing the redistributed Roslyn MIT notice.' }
$nuspecEntries = @($packageEntries | Where-Object { $_ -like '*.nuspec' })
if ($nuspecEntries.Count -ne 1) { throw "Tool package must contain exactly one NuGet manifest; found $($nuspecEntries.Count)." }
$nuspecEntry = $nuspecEntries[0]
$nuspecText = (tar -xOf $packagePath $nuspecEntry) -join "`n"
if ($nuspecText -match '<repository\b' -or $nuspecText -match '\bcommit=') { throw 'Tool package contains unreproducible repository metadata.' }

$localSourceXml = [Security.SecurityElement]::Escape($packagesPath)
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="phase1-local-package" value="$localSourceXml" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $nugetConfigPath -Encoding utf8NoBOM

$env:NUGET_PACKAGES = $nugetPackagesPath
$env:NUGET_HTTP_CACHE_PATH = $nugetHttpCachePath
$env:NUGET_PLUGINS_CACHE_PATH = $nugetPluginsCachePath
$env:DOTNET_CLI_HOME = $dotnetCliHomePath

dotnet tool install SourceGenAuditor.Tool --version $packageVersion --tool-path $toolPath --configfile $nugetConfigPath --no-cache
$toolExecutable = Join-Path $toolPath 'sourcegen-auditor.exe'
if (-not (Test-Path -LiteralPath $toolExecutable -PathType Leaf)) { throw 'Installed tool shim was not created.' }

try {
    $reportedVersion = (& $toolExecutable --version).Trim()
    if ($reportedVersion -ne $packageVersion) { throw "Expected tool $packageVersion, got $reportedVersion." }

    & $toolExecutable audit ./tests/scenarios/relevant/scenario.json --format json --output $relevantReportPath
    & $toolExecutable audit ./tests/scenarios/irrelevant/scenario.json --format json --output $irrelevantReportPath

    foreach ($reportPath in @($relevantReportPath, $irrelevantReportPath)) {
        $reportText = Get-Content -LiteralPath $reportPath -Raw
        $report = $reportText | ConvertFrom-Json
        if ($report.schemaVersion -ne 1 -or $report.verdict -ne 'PASS') { throw "Invalid passing report: $reportPath" }
        if (@($report.assertions).Count -ne 6 -or @($report.assertions | Where-Object result -ne 'PASS').Count -ne 0) { throw "Required assertions did not all pass: $reportPath" }
        if ($reportText -match '"sourceText"\s*:') { throw "Report contains full generated text: $reportPath" }
    }
}
finally {
    dotnet tool uninstall SourceGenAuditor.Tool --tool-path $toolPath
}

if (Test-Path -LiteralPath $toolExecutable) { throw 'Tool shim remains after uninstall.' }
```

Required outcome: every command and assertion exits successfully; the xUnit TRX has positive executed counts and the passing sentinel; the tool installs with only the just-created local package directory configured and fresh NuGet/Dotnet caches; both installed-tool audits are `PASS`; and uninstall removes the shim. `$packageSha256` is retained with the acceptance artifacts for provenance. This verifies only Windows, the single fixture, the two declared scenarios, host Roslyn 5.9.0, and package version 0.1.0. Linux, NuGet.org publishing, arbitrary generators, and lower Roslyn compatibility remain unverified.
