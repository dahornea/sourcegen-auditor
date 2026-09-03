#!/usr/bin/env sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository_root=$(dirname -- "$script_dir")
cd -- "$repository_root"

required_files='AGENTS.md
PRODUCT.md
ARCHITECTURE.md
PLAN.md
CODE_REVIEW.md
README.md
THIRD-PARTY-NOTICES.md
Directory.Build.props
.editorconfig
.gitignore
docs/scenarios.md
docs/decisions/README.md
docs/decisions/0001-controlled-scenario-and-compatibility.md
docs/decisions/0002-worker-process.md
docs/decisions/0003-evidence-verdict-and-reporting.md
docs/decisions/0004-testing-and-tool-packaging.md
eng/verify.ps1
eng/verify.sh
eng/recompute-vectors.ps1
global.json
Directory.Packages.props
SourceGenAuditor.slnx
src/SourceGenAuditor.Core/SourceGenAuditor.Core.csproj
src/SourceGenAuditor.Cli/SourceGenAuditor.Cli.csproj
tests/SourceGenAuditor.Tests/SourceGenAuditor.Tests.csproj
tests/SourceGenAuditor.Fixtures/SourceGenAuditor.Fixtures.csproj
.codex/config.toml
.codex/agents/roslyn-researcher.toml
.codex/agents/acceptance-designer.toml
.codex/agents/phase0-reviewer.toml'

failures=0

require_file() {
    if [ ! -s "$1" ]; then
        printf '%s\n' "Missing or empty required file: $1" >&2
        failures=$((failures + 1))
    fi
}

require_text() {
    if [ -f "$1" ] && ! grep -Fq -- "$2" "$1"; then
        printf '%s\n' "$1 is missing required text: $2" >&2
        failures=$((failures + 1))
    fi
}

for required_file in $required_files; do
    require_file "$required_file"
done

require_text ARCHITECTURE.md 'GO WITH CONDITIONS'
require_text ARCHITECTURE.md '35d9211b841e7613c1d2f8f5af6d628ace696c4c'
require_text ARCHITECTURE.md 'UNKNOWN'
require_text ARCHITECTURE.md 'not a security sandbox'
require_text ARCHITECTURE.md 'Microsoft Testing Platform v2'
require_text ARCHITECTURE.md 'sga-source-set-v1'
require_text ARCHITECTURE.md 'protocolVersion'
require_text ARCHITECTURE.md 'CheckpointEvidenceV1'
require_text ARCHITECTURE.md 'roslynReferences'
require_text ARCHITECTURE.md 'XunitMtpV2ProducesTrx'
require_text PRODUCT.md 'one declared controlled scenario'
require_text PRODUCT.md 'relevant'
require_text PRODUCT.md 'irrelevant'
require_text PLAN.md 'Architecture Gate and amendments F5-01/F5-02 are approved'
require_text PLAN.md '--report-xunit-trx'
require_text PLAN.md 'dotnet tool install'
require_text PLAN.md 'dotnet tool uninstall'
require_text PLAN.md '"rollForward": "disable"'
require_text PLAN.md '--configfile'
require_text ARCHITECTURE.md 'Amendments F5-01 and F5-02'
require_text ARCHITECTURE.md 'BeforeFirstLineDirective'
require_text docs/decisions/0003-evidence-verdict-and-reporting.md 'b2bbc1538dc65a17d1d7422f7d43e28bf739ad0afe1978b8db455c95fb1f0bb2'
require_text docs/decisions/0003-evidence-verdict-and-reporting.md '356c04407696fcd6a62d459d14743fbd1e60bd64a0e1dfb7f2b76b31c540bd8a'
require_text .codex/config.toml 'max_concurrent_threads_per_session = 2'
require_text .codex/agents/roslyn-researcher.toml 'sandbox_mode = "read-only"'
require_text .codex/agents/acceptance-designer.toml 'sandbox_mode = "read-only"'
require_text .codex/agents/phase0-reviewer.toml 'sandbox_mode = "read-only"'
require_text Directory.Build.props '<Project>'
require_text global.json '"version": "10.0.400"'
require_text global.json '"rollForward": "disable"'
require_text global.json '"runner": "Microsoft.Testing.Platform"'
require_text Directory.Packages.props '<PackageVersion Include="Microsoft.CodeAnalysis.CSharp" Version="5.9.0" />'
require_text Directory.Packages.props '<PackageVersion Include="xunit.v3.mtp-v2" Version="4.0.0" />'
require_text tests/SourceGenAuditor.Tests/SourceGenAuditor.Tests.csproj '<PackageReference Include="xunit.v3.mtp-v2" />'

project_count=$(find src tests -name '*.csproj' -type f | wc -l | tr -d ' ')
if [ "$project_count" -ne 4 ]; then
    printf '%s\n' "Expected exactly four Phase 1 projects, found $project_count." >&2
    failures=$((failures + 1))
fi

if grep -R -F -e 'Microsoft.NET.Test.Sdk' -e 'xunit.runner.visualstudio' -e 'TestingPlatformDotnetTestSupport' --include='*.csproj' src tests >/dev/null 2>&1; then
    printf '%s\n' 'Forbidden VSTest configuration found.' >&2
    failures=$((failures + 1))
fi

if [ -d .github/workflows ]; then
    printf '%s\n' 'CI workflows are outside Phase 1.' >&2
    failures=$((failures + 1))
fi

for adr_file in docs/decisions/0*.md; do
    require_text "$adr_file" '- Status: Accepted'
done

if [ "$failures" -ne 0 ]; then
    exit 1
fi

required_count=$(printf '%s\n' "$required_files" | wc -l | tr -d ' ')
printf '%s\n' "Phase 0 architecture verification passed: $required_count required files and the approved Phase 1 shape."
