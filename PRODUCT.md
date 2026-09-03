# Product specification

## Hypothesis

SourceGen Auditor audits a compiled Roslyn incremental source generator across a controlled A → B → A scenario. It combines exact generated-output comparison with Roslyn tracked-step reasons, exposing cases where output stayed equal but the pipeline still recomputed.

**Tagline:** Audit what your incremental generator recomputes—and what Roslyn reuses.

Every result describes observed behavior under one declared controlled scenario and is bounded to the selected generator, recorded public Roslyn evidence, and observed execution environment. It is not a global proof of determinism, semantic correctness, purity, performance, optimal incremental caching, compatibility, or behavior in every compiler host.

## User and problem

The initial user is a source-generator author who can provide a compiled generator and a controlled input scenario. The tool should turn an `A -> B -> A` experiment into reproducible observations and explicit assertion outcomes without guessing whether the change should matter.

## Completed Phase 1 deliverable

The completed first slice supports:

- C# on .NET 10;
- one selected `IIncrementalGenerator` from a compiled DLL;
- one manifest-defined scenario with one source-text replacement;
- an explicit `relevant` or `irrelevant` declaration;
- a Roslyn Common/CSharp 5.9.0.0 host that checks every Roslyn reference in the private dependency closure, rejects newer or unsupported components, admits supported lower-or-equal references for an observed attempt, and advertises compatibility only for the one fixture-covered closure;
- generated-source, generator-diagnostic, and tracked-step evidence;
- console and versioned JSON reports;
- deterministic-output, invalidation, and `A -> B -> A` restoration assertions where public evidence is complete.
- an installable framework-dependent `dotnet tool` package named `SourceGenAuditor.Tool`, version `0.1.0`, with command `sourcegen-auditor`.

## Claims the slice may make

- Two completed runs over the same declared controlled inputs produced equal or different canonical generated-source and generator-diagnostic snapshots.
- A declared output step was reported by Roslyn as `Cached`, `Unchanged`, `Modified`, `New`, or `Removed` for an adjacent run.
- A mutation declared irrelevant caused observed output invalidation or an output/diagnostic difference.
- A mutation declared relevant had no observable output-step invalidation.
- Restoring A did or did not restore A's canonical generated-source and generator-diagnostic snapshot.
- Generated sources were observably added, removed, renamed, or modified by hint name and exact content.

Every claim identifies its evidence. Incomplete evidence prevents `PASS`.

## Claims the slice must not make

- The generator is semantically correct, pure, safe, globally deterministic, or optimally incremental.
- A source is "stale" without an explicit scenario expectation that establishes that conclusion.
- An undeclared mutation is relevant or irrelevant.
- An untracked intermediate step was cached or invalidated.
- The compiler host, filesystem, clock, locale, environment, operating system, or generator side effects were fully controlled.
- The audit reproduces Visual Studio, `dotnet build`, or arbitrary solution behavior.
- Process isolation makes an untrusted generator safe.
- A successful audit proves that every generator built against the same or an older Roslyn version is compatible.

## Non-goals

- Visual Studio extension, dashboard, or GUI
- Automatic code fixes or general Roslyn profiling
- Performance benchmarking
- Automatic inference of mutation relevance
- Project/solution loading or arbitrary solution compatibility
- Multiple generators or languages
- Cloud, SaaS, accounts, AI, or paid services
- Execution of generators presented as malicious or untrusted

## Phase 1 completion record

Phase 1 completed after the exact sequence in `PLAN.md` passed locally. A user can pack the tool, install it from the locally produced package source into an isolated tool path, invoke `sourcegen-auditor` against the single approved generator fixture, and receive the same domain verdict and evidence in console and JSON projections. Acceptance covered success, assertion failure, unknown evidence, invalid scenarios, load failures, generator exceptions, timeouts, crashes, cancellation, TRX generation, package installation, worker self-spawn, and uninstall.
