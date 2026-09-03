[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repositoryRoot

$requiredFiles = @(
    'AGENTS.md'
    'PRODUCT.md'
    'ARCHITECTURE.md'
    'PLAN.md'
    'CODE_REVIEW.md'
    'README.md'
    'THIRD-PARTY-NOTICES.md'
    'Directory.Build.props'
    '.editorconfig'
    '.gitignore'
    'docs/scenarios.md'
    'docs/decisions/README.md'
    'docs/decisions/0001-controlled-scenario-and-compatibility.md'
    'docs/decisions/0002-worker-process.md'
    'docs/decisions/0003-evidence-verdict-and-reporting.md'
    'docs/decisions/0004-testing-and-tool-packaging.md'
    'eng/verify.ps1'
    'eng/verify.sh'
    'eng/recompute-vectors.ps1'
    'global.json'
    'Directory.Packages.props'
    'SourceGenAuditor.slnx'
    'src/SourceGenAuditor.Core/SourceGenAuditor.Core.csproj'
    'src/SourceGenAuditor.Cli/SourceGenAuditor.Cli.csproj'
    'tests/SourceGenAuditor.Tests/SourceGenAuditor.Tests.csproj'
    'tests/SourceGenAuditor.Fixtures/SourceGenAuditor.Fixtures.csproj'
    '.codex/config.toml'
    '.codex/agents/roslyn-researcher.toml'
    '.codex/agents/acceptance-designer.toml'
    '.codex/agents/phase0-reviewer.toml'
)

$failures = [System.Collections.Generic.List[string]]::new()

foreach ($relativePath in $requiredFiles) {
    $absolutePath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
        $failures.Add("Missing required file: $relativePath")
        continue
    }

    if ((Get-Item -LiteralPath $absolutePath).Length -eq 0) {
        $failures.Add("Required file is empty: $relativePath")
    }
}

foreach ($xmlPath in @(
    'Directory.Build.props'
    'Directory.Packages.props'
    'SourceGenAuditor.slnx'
    'src/SourceGenAuditor.Core/SourceGenAuditor.Core.csproj'
    'src/SourceGenAuditor.Cli/SourceGenAuditor.Cli.csproj'
    'tests/SourceGenAuditor.Tests/SourceGenAuditor.Tests.csproj'
    'tests/SourceGenAuditor.Fixtures/SourceGenAuditor.Fixtures.csproj'
)) {
    try {
        [xml](Get-Content -LiteralPath $xmlPath -Raw) | Out-Null
    }
    catch {
        $failures.Add("$xmlPath is not valid XML: $($_.Exception.Message)")
    }
}

$requiredText = @(
    @{ Path = 'ARCHITECTURE.md'; Pattern = 'GO WITH CONDITIONS' }
    @{ Path = 'ARCHITECTURE.md'; Pattern = '35d9211b841e7613c1d2f8f5af6d628ace696c4c' }
    @{ Path = 'ARCHITECTURE.md'; Pattern = 'UNKNOWN' }
    @{ Path = 'ARCHITECTURE.md'; Pattern = 'not a security sandbox' }
    @{ Path = 'ARCHITECTURE.md'; Pattern = 'Microsoft Testing Platform v2' }
    @{ Path = 'ARCHITECTURE.md'; Pattern = 'sga-source-set-v1' }
    @{ Path = 'ARCHITECTURE.md'; Pattern = 'protocolVersion' }
    @{ Path = 'ARCHITECTURE.md'; Pattern = 'CheckpointEvidenceV1' }
    @{ Path = 'ARCHITECTURE.md'; Pattern = 'roslynReferences' }
    @{ Path = 'ARCHITECTURE.md'; Pattern = 'XunitMtpV2ProducesTrx' }
    @{ Path = 'PRODUCT.md'; Pattern = 'one declared controlled scenario' }
    @{ Path = 'PRODUCT.md'; Pattern = 'relevant' }
    @{ Path = 'PRODUCT.md'; Pattern = 'irrelevant' }
    @{ Path = 'PLAN.md'; Pattern = 'Architecture Gate and amendments F5-01/F5-02 are approved' }
    @{ Path = 'PLAN.md'; Pattern = '--report-xunit-trx' }
    @{ Path = 'PLAN.md'; Pattern = 'dotnet tool install' }
    @{ Path = 'PLAN.md'; Pattern = 'dotnet tool uninstall' }
    @{ Path = 'PLAN.md'; Pattern = '"rollForward": "disable"' }
    @{ Path = 'PLAN.md'; Pattern = '--configfile' }
    @{ Path = 'ARCHITECTURE.md'; Pattern = 'Amendments F5-01 and F5-02' }
    @{ Path = 'ARCHITECTURE.md'; Pattern = 'BeforeFirstLineDirective' }
    @{ Path = 'docs/decisions/0003-evidence-verdict-and-reporting.md'; Pattern = 'b2bbc1538dc65a17d1d7422f7d43e28bf739ad0afe1978b8db455c95fb1f0bb2' }
    @{ Path = 'docs/decisions/0003-evidence-verdict-and-reporting.md'; Pattern = '356c04407696fcd6a62d459d14743fbd1e60bd64a0e1dfb7f2b76b31c540bd8a' }
    @{ Path = '.codex/config.toml'; Pattern = 'max_concurrent_threads_per_session = 2' }
)

foreach ($expectation in $requiredText) {
    if (Test-Path -LiteralPath $expectation.Path -PathType Leaf) {
        $content = Get-Content -LiteralPath $expectation.Path -Raw
        if (-not $content.Contains($expectation.Pattern)) {
            $failures.Add("$($expectation.Path) is missing required text: $($expectation.Pattern)")
        }
    }
}

try {
    $globalJson = Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json
    if ($globalJson.sdk.version -ne '10.0.400' -or
        $globalJson.sdk.rollForward -ne 'disable' -or
        $globalJson.sdk.allowPrerelease -ne $false -or
        $globalJson.test.runner -ne 'Microsoft.Testing.Platform') {
        $failures.Add('global.json does not match the approved SDK/MTP contract.')
    }
}
catch {
    $failures.Add("global.json is invalid: $($_.Exception.Message)")
}

[xml]$packageProps = Get-Content -LiteralPath 'Directory.Packages.props' -Raw
$packageVersions = @($packageProps.Project.ItemGroup.PackageVersion)
if ($packageVersions.Count -ne 2 -or
    -not ($packageVersions | Where-Object { $_.Include -eq 'Microsoft.CodeAnalysis.CSharp' -and $_.Version -eq '5.9.0' }) -or
    -not ($packageVersions | Where-Object { $_.Include -eq 'xunit.v3.mtp-v2' -and $_.Version -eq '4.0.0' })) {
    $failures.Add('Directory.Packages.props does not contain exactly the two approved package versions.')
}

$projectFiles = @(Get-ChildItem -LiteralPath 'src', 'tests' -Recurse -Filter '*.csproj' -File)
if ($projectFiles.Count -ne 4) {
    $failures.Add("Expected exactly four Phase 1 projects, found $($projectFiles.Count).")
}

$allProjectText = ($projectFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
foreach ($forbidden in @('Microsoft.NET.Test.Sdk', 'xunit.runner.visualstudio', 'TestingPlatformDotnetTestSupport')) {
    if ($allProjectText.Contains($forbidden)) {
        $failures.Add("Forbidden test configuration found: $forbidden")
    }
}

[xml]$testProject = Get-Content -LiteralPath 'tests/SourceGenAuditor.Tests/SourceGenAuditor.Tests.csproj' -Raw
$testPackages = @($testProject.SelectNodes('/Project/ItemGroup/PackageReference'))
if ($testPackages.Count -ne 1 -or $testPackages[0].Include -ne 'xunit.v3.mtp-v2') {
    $failures.Add('The test project must have xunit.v3.mtp-v2 as its only direct package.')
}

if (Test-Path -LiteralPath '.github/workflows') {
    $failures.Add('CI workflows are outside Phase 1.')
}

try {
    & (Join-Path $PSScriptRoot 'recompute-vectors.ps1') | Out-Null
}
catch {
    $failures.Add("Independent normative-vector recomputation failed: $($_.Exception.Message)")
}

foreach ($agentPath in $requiredFiles | Where-Object { $_ -like '.codex/agents/*.toml' }) {
    if (Test-Path -LiteralPath $agentPath -PathType Leaf) {
        $content = Get-Content -LiteralPath $agentPath -Raw
        if (-not $content.Contains('sandbox_mode = "read-only"')) {
            $failures.Add("Agent is not read-only: $agentPath")
        }
    }
}

$adrFiles = Get-ChildItem -LiteralPath 'docs/decisions' -Filter '*.md' -File |
    Where-Object { $_.Name -ne 'README.md' }
foreach ($adr in $adrFiles) {
    if (-not (Select-String -LiteralPath $adr.FullName -SimpleMatch '- Status: Accepted' -Quiet)) {
        $failures.Add("ADR is not accepted: docs/decisions/$($adr.Name)")
    }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Error $failure
    }
    exit 1
}

Write-Host "Phase 0 architecture verification passed: $($requiredFiles.Count) required files, the approved Phase 1 shape, and independent normative vectors."
