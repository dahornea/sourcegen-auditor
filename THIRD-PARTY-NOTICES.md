# Third-party notices

This inventory was generated from the repository's `net10.0` NuGet lock files. License expressions were independently read from the restored packages' `.nuspec` metadata. It is an engineering inventory, not legal advice.

## Runtime/tool dependencies

The installable `SourceGenAuditor.Tool` 0.1.0 package includes the following third-party dependency graph:

| Package | Resolved version | Relationship | SPDX license |
| --- | --- | --- | --- |
| `Microsoft.CodeAnalysis.CSharp` | 5.9.0 | direct in `SourceGenAuditor.Core`; centrally transitive in the tool | MIT |
| `Microsoft.CodeAnalysis.Common` | 5.9.0 | transitive | MIT |
| `Microsoft.CodeAnalysis.Analyzers` | 5.9.0-1.26328.17 | build-only transitive analyzer | MIT |

The Roslyn packages are developed at <https://github.com/dotnet/roslyn>. Their package metadata identifies the MIT license.

### Redistributed Roslyn binary notice

The following notice is reproduced from `ThirdPartyNotices.rtf` in the restored `Microsoft.CodeAnalysis.Common` 5.9.0 and `Microsoft.CodeAnalysis.CSharp` 5.9.0 packages:

> The MIT License (MIT)
>
> Copyright (c) .NET Foundation and Contributors All rights reserved.
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

The tool is framework-dependent and does not bundle the .NET SDK or .NET runtime. Their separate product and component terms apply.

## Test/build-only dependencies

These packages are resolved by `tests/SourceGenAuditor.Tests/packages.lock.json`; they are not runtime dependencies of the packed tool:

| Package | Resolved version | Relationship | SPDX license |
| --- | --- | --- | --- |
| `xunit.v3.mtp-v2` | 4.0.0 | direct | Apache-2.0 |
| `xunit.v3.core.mtp-v2` | 4.0.0 | transitive | Apache-2.0 |
| `xunit.v3.assert` | 4.0.0 | transitive | Apache-2.0 |
| `xunit.v3.common` | 4.0.0 | transitive | Apache-2.0 |
| `xunit.v3.extensibility.core` | 4.0.0 | transitive | Apache-2.0 |
| `xunit.v3.runner.common` | 4.0.0 | transitive | Apache-2.0 |
| `xunit.v3.runner.inproc.console` | 4.0.0 | transitive | Apache-2.0 |
| `xunit.analyzers` | 2.0.0 | transitive analyzer | Apache-2.0 |
| `Microsoft.Testing.Platform` | 2.3.3 | transitive | MIT |
| `Microsoft.Testing.Platform.MSBuild` | 2.3.3 | transitive | MIT |
| `Microsoft.Testing.Extensions.Telemetry` | 2.3.3 | transitive | MIT |
| `Microsoft.Testing.Extensions.TrxReport.Abstractions` | 2.3.3 | transitive | MIT |
| `Microsoft.ApplicationInsights` | 2.23.0 | transitive | MIT |
| `Microsoft.Bcl.AsyncInterfaces` | 6.0.0 | transitive | MIT |
| `Microsoft.Win32.Registry` | 5.0.0 | transitive | MIT |
| `System.Security.AccessControl` | 6.0.1 | transitive | MIT |

xUnit.net packages are developed at <https://github.com/xunit/xunit>. Microsoft Testing Platform packages are developed at <https://github.com/microsoft/testfx>. The remaining Microsoft packages identify their respective Microsoft repositories in their NuGet metadata.

## Authoritative package metadata

Each NuGet package carries its own license metadata and, where applicable, license file. Those package artifacts are authoritative for the corresponding third-party component. The workspace lock files are authoritative for the exact graph restored and verified for this release.
