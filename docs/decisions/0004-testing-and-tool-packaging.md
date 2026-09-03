# ADR 0004: Testing and dotnet-tool packaging

- Status: Accepted
- Date: 2026-09-02

## Context

xUnit.net v3 4.0 defaults to Microsoft Testing Platform v2. Mixing that model with VSTest packages or VSTest-only command switches would make the project configuration and acceptance sequence contradictory. The Phase 1 deliverable also needs an explicit distribution boundary.

## Decision

Use .NET SDK 10.0.400 with `global.json` setting `rollForward` to `disable` and selecting `Microsoft.Testing.Platform`. The single executable test project targets `net10.0`, sets `OutputType` to `Exe`, `IsTestProject` to `true`, and `IsPackable` to `false`, and references only `xunit.v3.mtp-v2` 4.0.0 as its direct test package. That package selects `xunit.v3.core.mtp-v2`/assertion/runtime packages at 4.0.0 and MTP v2 packages with a 2.3.3 minimum; the committed package lock graph fixes the exact resolved transitives. Do not reference `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio`; VSTest is not selected. Do not set `TestingPlatformDotnetTestSupport`, which the .NET 10 MTP mode does not require.

Run tests with `dotnet test --solution` and the xUnit MTP writer's `--report-xunit-trx` options. The result must be a 2010-namespace TRX `TestRun` with positive total/executed counters and the exact named xUnit/MTP sentinel marked `Passed`; XML parsing or exit zero alone is insufficient.

Phase 1 delivers a framework-dependent .NET tool: package ID `SourceGenAuditor.Tool`, version `0.1.0`, command `sourcegen-auditor`. The CLI project sets `PackAsTool=true` and `ToolCommandName=sourcegen-auditor`. Acceptance packs and hashes the project, creates a NuGet config whose only source is that fresh local package directory, uses fresh NuGet/Dotnet caches and an isolated `--tool-path`, invokes version and both audit scenarios through the installed shim, uninstalls it, and verifies the shim was removed. Publishing to NuGet.org is not part of Phase 1.

## Consequences

There is one test engine and one command-line grammar. The test package supplies MTP v2 transitively and its own TRX writer, so no VSTest or separate TRX-extension package is needed. Package-install and worker self-spawn failures block Phase 1 acceptance. Public publishing, signing, RID-specific packaging, self-contained tools, and release automation remain deferred.
