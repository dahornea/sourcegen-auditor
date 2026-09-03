# Architecture Gate specification

Status: **Approved for Phase 1; amendments F5-01 and F5-02 accepted**
Research baseline: **2026-09-02**

Amendments F5-01 and F5-02 were owner-approved on 2026-09-03. Diagnostic canonicalization V1 consumes values observable through the public Roslyn 5.9.0 API after Roslyn's own normalization. It does not reconstruct constructor arguments that the API no longer exposes. Because V1 is unreleased, F5-02 directly replaces its source-location encoding with lossless discriminated mapped-path, path-identity, and line-visibility values; no V2 is introduced.

## Feasibility

**GO WITH CONDITIONS.** Public Roslyn APIs expose enough evidence for a narrow C# audit, provided Phase 1 pins the host to Roslyn 5.9.0, requires explicit scenario expectations, compares independent cold runs, reuses every returned `GeneratorDriver` for adjacent transition evidence, runs selected code in a killable worker process, uses the locked evidence/protocol contracts below, delivers and smoke-tests an installable .NET tool, and stops at every falsification gate rather than improvising.

The architecture does not support a claim of semantic correctness, historical cache reuse, or safe execution of hostile code.

## Approved gate decisions

| Review blocker | Locked decision |
| --- | --- |
| Test execution | Microsoft Testing Platform v2 through `xunit.v3.mtp-v2` 4.0.0; no VSTest packages or switches |
| Roslyn references | Host Common/CSharp 5.9.0.0; inspect the full private dependency closure, admit each supported component at lower/equal, reject higher or unsupported Roslyn components; compatibility claims only for the executable fixture-covered closure |
| Deliverable | Installable framework-dependent `SourceGenAuditor.Tool` 0.1.0 with command `sourcegen-auditor`; public publishing deferred |
| Canonical comparison | Exact source/diagnostic identity, Unicode, encoding/BOM, framing, ordering, duplicate, location, and redaction rules below are versioned contracts |
| Worker protocol | Two-pipe version 1 protocol with exact framing, sizes, deadlines, malformed-message behavior, stream capture, temp lifecycle, and cleanup rules below |
| Feasibility | F1-F7 are mandatory stop/re-gate criteria; F2 failure makes Phase 1 `NO-GO` |
| Public claim | Audit of observed behavior for one declared controlled scenario only; no global determinism or optimal-caching proof |

## Phase 1 boundary

Phase 1 accepts a versioned scenario manifest that identifies one compiled generator DLL and one fully qualified `IIncrementalGenerator` type. It constructs a C# compilation directly. It does not evaluate a project, invoke MSBuild, or use `MSBuildWorkspace`.

Every verdict describes observed behavior of that selected generator under that one declared controlled scenario and recorded environment only. Neither the CLI nor its reports may shorten that claim into global determinism, correctness, compatibility, or optimal incremental caching.

The only mutation is replacement of one declared C# source text. Metadata references and compiler options are explicit and fixed for the scenario. Additional texts, analyzer-config mutations, parse-option mutations, compilation-option mutations, and metadata-reference mutations are researched but deferred.

The Phase 1 runtime is framework-dependent .NET 10.0, built with SDK 10.0.400 selected with `rollForward: disable`. The host/compiler dependency is Microsoft.CodeAnalysis.CSharp 5.9.0, whose host-unified assemblies for Phase 1 are exactly `Microsoft.CodeAnalysis` and `Microsoft.CodeAnalysis.CSharp`, both assembly version 5.9.0.0. Every reference to either simple name in the metadata-only target/private-dependency closure is checked before code loads. A requested component version greater than its matching host component is rejected; an equal or lower version is admitted for an observed load attempt. Any other `Microsoft.CodeAnalysis*` assembly reference is unsupported and rejected. This numeric admission rule is not an advertised compatibility range. Phase 1's single generator fixture and its closure reference the two supported components at 5.9.0.0, so only that complete equal-version closure is fixture-covered; any lower component is explicitly `NotFixtureCovered` and gains no compatibility claim from the product.

## System shape

```text
Public CLI
  -> scenario validation and input fingerprinting
  -> worker supervisor
       -> cold worker: A
       -> transition worker: A -> B -> restored A -> stable A
            -> named generator loader
            -> Roslyn adapter with tracking enabled
            -> framed evidence checkpoints
  -> pure assertion evaluator
  -> versioned report mapper -> JSON renderer
  -> console renderer
  -> exit-code mapper
```

The cold and transition workers are sequential. The first A and the transition worker's initial A use fresh processes and fresh drivers, so their equality is stronger evidence than a warm cached rerun. It is still evidence only for the observed environments, not universal determinism.

Within the transition worker, code must retain every immutable driver returned by creation updates and runs:

```text
driverA = driver0.RunGenerators(A)
driverB = driverA.RunGenerators(B)
driverA2 = driverB.RunGenerators(restoredA)
driverA3 = driverA2.RunGenerators(stableA)
```

Roslyn compares each run with the immediately previous driver state. The restored-A run is compared with B and is not expected to be `Cached`; its canonical output is compared with the initial A. The final unchanged A run establishes whether the restored state becomes cached.

## Minimum solution structure after approval

```text
SourceGenAuditor.slnx
global.json
src/
  SourceGenAuditor.Core/       domain, canonicalization, Roslyn adapter, report DTOs
  SourceGenAuditor.Cli/        CLI, worker mode, supervision, rendering, exit mapping
tests/
  SourceGenAuditor.Tests/      unit, adapter, worker, CLI, and acceptance tests
  SourceGenAuditor.Fixtures/   test-only incremental generators
```

The CLI executable self-spawns through a hidden internal worker mode; a third production project is unnecessary. Domain types contain no ANSI text, JSON attributes, or process exit codes.

## Official research findings

### Public evidence APIs

Create `CSharpGeneratorDriver` with `GeneratorDriverOptions.TrackIncrementalGeneratorSteps` enabled, capture the driver returned from each run, and call `GetRunResult()`. `GeneratorDriverRunResult.Results` exposes a `GeneratorRunResult` per selected generator. Public evidence includes:

- `GeneratedSources`, whose records expose `HintName`, `SourceText`, and `SyntaxTree`;
- generator `Diagnostics` and `Exception`;
- `TrackedSteps` and `TrackedOutputSteps`;
- `IncrementalGeneratorRunStep.Name`, `Inputs`, `Outputs`, and `ElapsedTime`;
- output reasons `New`, `Modified`, `Unchanged`, `Cached`, and `Removed`.

`Unchanged` means modified input was recomputed to an equal step value. `Cached` means the input was unchanged and the output came from the step cache. Only `Cached` satisfies a no-regeneration assertion. Timing is observed metadata only and never affects a verdict.

### Tracking names

`WithTrackingName` creates stable author-selected names for intermediate providers. It is required for author-addressable intermediate assertions, but not for final registered-output evidence. Roslyn 5.9.0 also supplies well-known input names (`Compilation`, `ParseOptions`, `AdditionalTexts`, `AnalyzerConfigOptions`, `MetadataReferences`) and output names (`SourceOutput`, `ImplementationSourceOutput`, `PreCompilationSourceOutput`). Unnamed intermediate nodes may be reachable through the input graph, but SourceGen Auditor will not turn that unstable graph position into a named correctness claim.

Without author tracking names, Phase 1 still reports generated sources, generator diagnostics, exceptions, built-in inputs, and registered output steps. It reports intermediate cache behavior as unavailable rather than guessing.

### Inputs and diagnostics

- Syntax pipelines observe compilation syntax through `SyntaxProvider`. Phase 1 preserves unchanged tree instances and replaces only the declared logical tree.
- `AdditionalTextsProvider` observes additional-text additions, removals, and modifications; a future slice must update both compilation inputs and the driver through `AddAdditionalTexts`, `RemoveAdditionalTexts`, or `ReplaceAdditionalText(s)`.
- `AnalyzerConfigOptionsProvider` is a distinct driver input updated through `WithUpdatedAnalyzerConfigOptions`. Roslyn exposes the provider as a unit, so per-key invalidation precision cannot be claimed from that input alone.
- `ParseOptionsProvider` is distinct driver state updated through `WithUpdatedParseOptions`; parse options also control parsing generated source.
- `MetadataReferencesProvider` exposes reference changes. Compilation references must be changed on the immutable compilation and explicit reference files must be fingerprinted.
- Compilation options are publicly observable to generators through `CompilationProvider` and `Compilation.Options`; Roslyn 5.9.0 does not expose the internal compilation-options provider as a public dedicated provider.
- Diagnostics are outputs, not a dedicated incremental input. A generator can derive behavior by querying its compilation, including compilation diagnostics. Reports keep generator diagnostics, Roslyn generator-failure diagnostics, input-compilation diagnostics, post-generation compilation diagnostics, and tool failures in separate categories.

### Generator reuse

`GeneratorDriver` is immutable. Reusing the returned driver exposes caching between adjacent runs; recreating or discarding it loses that evidence. Pinned Roslyn source retains the immediately previous state table, not a public historical memo of every prior input. Therefore A restoration is established by canonical output comparison, while B-to-A run reasons describe only that adjacent transition.

### Loading and compatibility

`AnalyzerFileReference` plus `IAnalyzerAssemblyLoader` is the public compiler-host discovery path and exposes `AnalyzerLoadFailed`, but its generator APIs return `ISourceGenerator`; Roslyn wraps incremental generators and does not preserve the original implementation type as the returned public runtime type. It is therefore not the Phase 1 selection mechanism for a manifest's exact `typeName`.

The public CLI acquires read-only, sharing-denying handles for the manifest, every declared source/reference/replacement, and every DLL in the generator directory, reloads and revalidates the complete scenario through those handles, and retains the lease until both workers finish. This prevents ordinary pathname replacement or overwrites from changing the parent-approved byte set between workers on the Phase 1 Windows host; it is consistency evidence, not protection from arbitrary generator code or actors that already possess write-capable handles. Each worker also verifies the declared hashes before execution.

The worker first builds a recursive metadata-only closure from the leased target DLL bytes through every file-backed managed private dependency resolved by exact simple name in the generator directory; duplicate identities, unresolved references, and ambiguity are typed load errors. Before loading code, it enumerates every assembly reference in that closure whose simple name begins `Microsoft.CodeAnalysis`. `Microsoft.CodeAnalysis` and `Microsoft.CodeAnalysis.CSharp` are the only host-supported names and each requested version is compared with that same host assembly's 5.9.0.0 version; any higher occurrence is rejected. A direct `Microsoft.CodeAnalysis` reference is required. Any other Roslyn simple name, including Workspaces assemblies, is unsupported and rejected. Strict equality is not technically necessary: .NET permits an already loaded assembly to satisfy an equal-or-lower request. That platform rule does not prove Roslyn binary compatibility for every older version.

A worker-local `AssemblyLoadContext` then unifies exactly `Microsoft.CodeAnalysis` and `Microsoft.CodeAnalysis.CSharp` with the corresponding host instances and probes the already inspected file-backed private closure only from the generator directory. It resolves the manifest's exact fully qualified type from the target assembly, requires one concrete instantiable type implementing `IIncrementalGenerator`, creates it, and converts that selected instance through the public `AsSourceGenerator` adapter. Loading a second Roslyn copy would break type identity.

Phase 1 accepts only a managed generator compatible with .NET 10 whose closure contains no unsupported or newer Roslyn assembly reference, whose manifest hash matches, whose named type is discoverable for C#, and whose underlying type implements `IIncrementalGenerator`. A missing required Common reference, unsupported/newer Roslyn component, legacy-only `ISourceGenerator`, mismatched type, missing/ambiguous dependency, bad image, or framework incompatibility is a typed load/compatibility `ERROR`.

Roslyn itself checks and rejects references newer than its running compiler, while .NET assembly loading allows equal-or-higher loaded versions to satisfy lower requests. The report records an ordinally sorted `roslynReferences` array with `{ referencingAssemblySha256, simpleName, requestedVersion, hostVersion, admissionDecision }` for every closure occurrence. Decision is `EqualHost`, `LowerThanHost`, `RejectedNewer`, or `RejectedUnsupportedComponent`; the aggregate is rejected if any entry is rejected. `fixtureCoverage` is `Covered` only when the target bytes have approved SHA-256 `0f22ceda1bb8d75701a962c325b68f9dc0fd202018bea4e0f170a48b88da3fa1`, the assembly/type identities match the fixture, and every supported-component entry is exactly 5.9.0.0; otherwise it is `NotFixtureCovered`. A successful lower-version run supports only the observed artifact/scenario result. Future advertised version ranges require executable fixtures and a new gate; future multi-host support should use separately versioned workers rather than side-by-side Roslyn copies.

## Execution and failure model

Each worker has two anonymous pipes: evidence from worker to parent and control from parent to worker. An evidence frame is a four-byte unsigned big-endian payload length followed by one strict UTF-8 JSON object without BOM. The closed envelope is `{ protocolVersion, type, sequence, payload }`: version is the JSON integer `1`; sequences are JSON integers beginning at zero and contiguous; and every envelope and payload object rejects unknown or duplicate properties. Evidence payloads are closed by type:

- `hello`: `{ workerKind, expectedCheckpointIds }`, where `workerKind` is `cold` or `transition` and the ordered checkpoint IDs must exactly equal the parent request;
- `admission`: `{ roslynReferences, aggregateAdmissionDecision, fixtureCoverage }`, using `CompatibilityEvidenceV1` below;
- `checkpoint`: `{ checkpointId, completion, evidence }`, where the next expected ID is required and `completion` is `Complete` or `Partial`;
- `completed`: `{ completedCheckpointIds }`, which is terminal and must exactly name the already accepted checkpoints in order;
- `failure`: `{ failureKind, message, activeCheckpointId }`, which is terminal; `activeCheckpointId` is a string or null. Worker-emitted `failureKind` is exactly `GeneratorException`, `LoadFailure`, `CompatibilityFailure`, `Canceled`, `EvidenceLimitExceeded`, `CanonicalizationFailure`, or `InternalFailure`; timeout, crash, protocol, and report-write failures are parent-synthesized.

Control permits exactly one envelope `{ protocolVersion: 1, type: "cancel", sequence: 0, payload: { reason: "UserCancellation" | "Timeout" } }`. Each strict JSON frame body is at most 8 MiB, including its envelope, and the sum of all evidence frame bodies from one worker is at most 32 MiB. String enums are ordinal and case-sensitive. Evidence values use the versioned domain/report shapes defined above; a protocol-version change is required before any envelope or payload shape changes.

`checkpoint.evidence` is the closed `CheckpointEvidenceV1` object below. All properties are required, including nullable ones. JSON integers are nonnegative and no greater than 9,007,199,254,740,991; hashes are exactly 64 lowercase hexadecimal characters; Base64 is canonical RFC 4648 with padding; strings contain valid Unicode; and the enclosing frame limits remain authoritative.

```text
CheckpointEvidenceV1: {
  evidenceSchemaVersion: integer 1,
  runId: coldA | transitionA | mutatedB | restoredA | stableA,
  environment: {
    runtimeVersion: string,
    osDescription: string,
    processArchitecture: string,
    culture: string,
    uiCulture: string,
    timeZoneId: string,
    roslynHost: [ { simpleName, assemblyVersion, moduleVersionId } ],
    privateDependencies: [ { simpleName, pathToken, sha256 } ]
  },
  sources: SourceSnapshotV1,
  generatorDiagnostics: DiagnosticSnapshotV1,
  roslynFailureDiagnostics: DiagnosticSnapshotV1,
  inputCompilationDiagnostics: DiagnosticSnapshotV1,
  postGenerationCompilationDiagnostics: DiagnosticSnapshotV1,
  trackedSteps: TrackedStepsV1,
  generatorException: null | { typeName, message, stackTrace: string|null }
}
SourceSnapshotV1: {
  availability: Available | Unavailable,
  unavailableReason: null | MissingPublicEvidence,
  records: [ {
    hintName, textUtf8Base64, utf16Length, encodingName: string|null,
    preambleLength, checksumAlgorithm, roslynChecksumHex, contentSha256
  } ],
  setSha256: string|null
}
DiagnosticSnapshotV1: {
  availability: Available | Unavailable,
  unavailableReason: null | UnsupportedLocationKind | MissingPublicEvidence,
  records: [ {
    id, severity, isWarningAsError, isSuppressed, warningLevel, invariantMessage,
    descriptorCategory, descriptorDefaultSeverity, helpLinkUri: string,
    customTags: (string|null)[], primaryLocation: LocationV1,
    additionalLocations: LocationV1[],
    properties: [ { key, value: string|null } ],
    occurrenceCount, canonicalRecordBase64
  } ],
  setSha256: string|null
}
TrackedStepsV1: {
  availability: Available | Unavailable,
  unavailableReason: null | MissingPublicEvidence,
  steps: [ {
    name, occurrence,
    inputs: [ { sourceStepName, sourceOccurrence, outputIndex } ],
    outputs: [ { index, reason: New|Modified|Unchanged|Cached|Removed } ]
  } ]
}
```

`CompatibilityEvidenceV1.roslynReferences` contains exact objects `{ referencingAssemblySha256, simpleName, requestedVersion, hostVersion: string|null, admissionDecision }`. Versions are four nonnegative decimal components in canonical no-leading-zero form. Entries are sorted by ordinal simple name, numeric four-part requested version, then ordinal referencing hash. `admissionDecision` is `EqualHost`, `LowerThanHost`, `RejectedNewer`, or `RejectedUnsupportedComponent`; aggregate decision is `Admitted` or `Rejected`; fixture coverage is `Covered` or `NotFixtureCovered`. Both workers must send byte-equivalent admission payloads; a mismatch is `WorkerProtocolFailure`. A rejected admission must be followed by terminal `CompatibilityFailure`; a metadata/load failure before admission is terminal `LoadFailure`, and the parent publishes an empty compatibility list with aggregate `Unavailable` and `NotFixtureCovered`.

In the checkpoint notation, unannotated scalar names are strings except booleans named `is...` and integer/count/index/length fields. `UnmappedPathValueV1` is `{ kind: "Controlled" | "Generated" | "External", token: string }` with a nonempty token. `MappedPathPayloadV1` is `{ kind: "Empty", token: "" }` or `{ kind: "External", token: string }`, with a nonempty token in the external variant. `MappedPathV1` is the closed discriminated union `{ hasMappedPath: false }` or `{ hasMappedPath: true, value: MappedPathPayloadV1 }`; the false variant has no `value` property, and the true variant always has one. `LocationV1` is exactly `{ kind: "None" }` or `{ kind: "SourceFile", unmappedPath: UnmappedPathValueV1, utf16SpanStart, utf16SpanLength, mappedPath: MappedPathV1, mappedStartLine, mappedStartColumn, mappedEndLine, mappedEndColumn, lineVisibility: "Visible" | "Hidden" | "BeforeFirstLineDirective" }`. The domain uses separate unmapped and mapped-payload types, so `Empty` cannot be an unmapped path and `Controlled` or `Generated` cannot be a mapped payload. An unsupported but otherwise valid location kind never appears as `LocationV1`: it makes that complete diagnostic snapshot `Unavailable` with `UnsupportedLocationKind`. `textUtf8Base64` is the strict-UTF-8 source text, `contentSha256` hashes those decoded bytes, `canonicalRecordBase64` decodes to the exact diagnostic-record bytes, `moduleVersionId` is lowercase UUID `D` format, and every checksum field is lowercase hexadecimal. Available snapshots require a null reason, canonical record order, and non-null set hash; unavailable snapshots require an empty record array and null set hash. The parent recomputes every transmitted Base64/hash/count invariant before accepting a checkpoint. Full source text is present only as private protocol Base64 and is omitted from the public report.

`environment.roslynHost` is sorted by ordinal `simpleName`. Every private dependency must resolve inside the canonical generator directory; its `pathToken` is `private:` plus the `/`-separated `Path.GetRelativePath` from that directory, with empty, rooted, `.`/`..`, and escaping results rejected. `privateDependencies` is sorted by ordinal `pathToken`, then ordinal `simpleName`, then ordinal lowercase hash. Diagnostic arrays use the canonical record-byte orders; source records use ordinal hint-name order; tracked steps use ordinal name, occurrence, then numeric relationship/output order.

The cold worker's expected IDs are exactly `[coldA]`; the transition worker's are `[transitionA, mutatedB, restoredA, stableA]`. `hello` is sequence zero and a successful metadata inspection produces exactly one `admission` before any checkpoint. A `Complete` checkpoint requires available sources and a null `generatorException`; a diagnostic snapshot may be unavailable only for `UnsupportedLocationKind` or `MissingPublicEvidence`, and tracked steps only for `MissingPublicEvidence`, yielding `UNKNOWN` where required. A `completed` terminal is valid only after admission and every expected checkpoint arrived once with `completion: Complete`. A `Partial` checkpoint requires at least one available snapshot and must be immediately followed by terminal `failure` naming that same active checkpoint; `generatorException` is non-null exactly when `failureKind` is `GeneratorException`. This path produces aggregate `ERROR`. Terminal `failure` before the next checkpoint causes the parent to synthesize that run as `Unavailable` and also produces `ERROR`. A worker may never send a checkpoint whose completion is `Unavailable`. Malformed paths, coordinates, Unicode, canonical bytes, or hashes are `CanonicalizationFailure`, require `Partial -> failure` or direct `failure`, and can never be represented as snapshot unavailability in a complete checkpoint.

Unknown versions, types, properties, enum values, checkpoint IDs, or counts; duplicate properties; invalid UTF-8 or JSON; non-integer, negative, duplicate, or skipped sequences; an oversize length; EOF inside a prefix or payload; a frame after a terminal frame; trailing bytes after the terminal frame; or a missing terminal frame are `WorkerProtocolFailure` and produce `ERROR`. `hello` must be sequence zero, exactly one terminal frame is required, and EOF is required after it. Previously validated checkpoints survive; a partial frame never does.

The default deadline is 30 seconds for startup and each expected checkpoint, configurable from 1 through 600 seconds. A deadline resets only after a complete validated checkpoint; partial bytes do not extend it. The absolute worker deadline is `(expected checkpoint count + 1) * timeout`. On timeout or user cancellation, the parent sends `cancel`, waits two seconds, calls `Kill(entireProcessTree: true)` if necessary, then waits at most five seconds for the root. Timeout is exit 3. A user cancellation is exit 130 only if the root exits within cleanup; failure to terminate the root overrides cancellation as `InternalFailure`, aggregate `ERROR`, exit 3. Descendant cleanup is best effort.

Before selected code runs, the Windows worker clears handle inheritance on its evidence, control, stdout, and stderr handles so an ordinary descendant cannot retain them. Stdout and stderr are separate redirected byte streams and are drained concurrently. For each, the parent retains at most the first 1 MiB, hashes all drained bytes with SHA-256, records total/captured/discarded counts and truncation, and represents retained bytes as Base64 in the report. Logs cannot enter the evidence pipe or affect an assertion.

The public report is strict UTF-8 JSON without BOM and may not exceed 32 MiB. Evidence overflow is `EvidenceLimitExceeded`; report overflow is `ReportWriteFailure`. IPC creates no temporary file. `--output` uses one invocation-owned same-directory `<target>.sga-tmp-<process-id>-<random>` file, flushes it, then atomically moves or replaces it. The parent deletes only that exact owned path on success, error, or cancellation and never wildcard-cleans; abrupt parent termination can leave an orphan.

Completed checkpoints are preserved after a generator exception, timeout, cancellation, worker crash, protocol failure, canonicalization failure, or report-write failure. An incomplete active checkpoint never supplies `PASS` evidence.

Pinned Roslyn 5.9.0 source permits some pre-compilation generated sources to survive a later generation failure even though older API remarks describe exception results more narrowly. Phase 1 records the version-specific partial facts and marks affected assertions `ERROR`; it does not discard or promote partial sources.

## Domain contracts

- `ScenarioDefinition`: schema version, generator target, controlled A inputs, one mutation, and explicit expectations.
- `GeneratorTarget`: assembly path, SHA-256, and fully qualified type name.
- `ControlledInputSet`: logical source paths, explicit reference paths and hashes, parse options, and compilation options.
- `MutationDefinition`: ID, source replacement, declared relevance, and expected source/diagnostic deltas.
- `RunEvidence`: checkpoint ID, completion (`Complete`, `Partial`, `Unavailable`), environment fingerprint, source snapshot, categorized diagnostics, tracked graph, stdout/stderr metadata, and failure.
- `ObservedFact`: immutable fact with an evidence ID; no pass/fail language.
- `AssertionResult`: `PASS`, `FAIL`, `UNKNOWN`, or `ERROR`, required evidence IDs, and a stable reason code.
- `AuditResult`: ordered observations, assertions, aggregate verdict, and partial-evidence flag.
- `AuditReportV1`: public JSON projection. It is separate from the internal worker protocol.

Aggregate precedence is `ERROR > FAIL > UNKNOWN > PASS`. `PASS` requires every required assertion to be `PASS`. `UNKNOWN` means completed public evidence cannot establish a required claim. An operational failure to obtain or complete evidence is `ERROR`.

## Canonical evidence

### Generated sources

- Identity is the exact `HintName`, compared ordinally and case-sensitively.
- Content equality is ordinal equality over the exact `SourceText` UTF-16 code-unit sequence. Whitespace, normalization form, line endings, and a literal U+FEFF code unit are significant.
- `SourceText.Encoding`, its preamble/BOM behavior, and Roslyn checksum metadata are observations only. They do not affect equality or the canonical hash, and the tool does not claim that original-byte BOM presence is always recoverable from public APIs.
- Duplicate hint names are `ERROR`, never last-write-wins.
- Canonical strings use strict UTF-8 without BOM. `frame(x)` is an unsigned 64-bit big-endian byte length followed by `x`. A record is `frame(UTF8("sga-source-v1")) + frame(UTF8(hintName)) + frame(UTF8(text))`. The set is `frame(UTF8("sga-source-set-v1")) + UInt64BE(recordCount) + frame(record)` for records sorted by ordinal hint name. Its fingerprint is SHA-256 of those complete set bytes. An unpaired surrogate that strict UTF-8 cannot encode is `CanonicalizationFailure`.
- Record character length, encoding name/null, encoding preamble length, Roslyn checksum algorithm, and Roslyn checksum are observations. `SourceText.GetChecksum()` is not the product equality hash because Microsoft documents that it can reflect original encoded bytes.
- The public report includes hint name, lengths, metadata, and hashes, not full generated text.

Source diffs are derived by hint-name map: an absent/present key is added or removed, the same key with a different content fingerprint is modified, and a rename appears as remove plus add. A source is not called stale without a matching explicit expectation.

### Diagnostics

Generator diagnostics form a multiset. The canonical primitives are locked as follows: `u64(n)` is an unsigned 64-bit big-endian integer; `bool(x)` is one byte `00` or `01`; `str(s)` is `00` for null or `01 + frame(strict-UTF8(s))` otherwise; and `seq(items)` is `u64(count) + frame(item)` for every item. No Unicode normalization or case folding occurs. Enums use their exact case-sensitive contract names through `str`; nonnegative numeric values use `u64`.

A canonical diagnostic record is the concatenation below, in order. Its values are read from the public Roslyn 5.9.0 API after Roslyn normalization; the canonical record never claims to recover an original constructor argument:

```text
frame(UTF8("sga-diagnostic-v1"))
str(Diagnostic.Id) + str(Diagnostic.Severity.ToString())
bool(isWarningAsError) + bool(isSuppressed) + u64(warningLevel)
str(GetMessage(CultureInfo.InvariantCulture))
str(Diagnostic.Descriptor.Category)
str(Diagnostic.Descriptor.DefaultSeverity.ToString())
str(non-null Diagnostic.Descriptor.HelpLinkUri)
seq(customTags sorted ordinal, duplicates preserved)
frame(primaryLocation)
seq(additionalLocations sorted by unsigned canonical bytes, duplicates preserved)
seq(properties sorted by ordinal key then null-first ordinal value;
    each item = str(key) + str(value))
```

Equal record bytes collapse to one entry with an occurrence count; duplicates are never discarded. The diagnostic set is `frame(UTF8("sga-diagnostic-set-v1")) + u64(uniqueRecordCount) + frame(record) + u64(occurrenceCount)` for every unique record sorted by unsigned lexicographic record bytes. SHA-256 hashes the complete diagnostic-set bytes and is rendered as lowercase hexadecimal.

The F5-01 normalization audit locks these observable-value rules:

| Public surface | Roslyn 5.9.0 behavior | V1 treatment |
| --- | --- | --- |
| `DiagnosticDescriptor.HelpLinkUri` | a null constructor argument is exposed as `String.Empty` | encode the observed empty string through the non-null `str` branch; the null marker is invalid for this field |
| `DiagnosticDescriptor.Description` | a null constructor argument is exposed as an empty `LocalizableString` | audited but excluded from the locked diagnostic record; do not claim its original constructor value |
| descriptor ID and category | null is rejected; ID also rejects empty or whitespace and accepted ID text is not trimmed | include the observed ID and category |
| descriptor title and message format in the string overload | null and empty both become the same non-null empty `LocalizableString`; the `LocalizableString` overload rejects a null object | exclude title, raw message format, and arguments; include only the resulting invariant-culture `GetMessage` value, so a normalized empty message is encoded as an observed non-null empty string |
| descriptor custom-tags container | a null container becomes an empty immutable array; supplied elements are retained | encode the observed array; retain null and empty element distinction through `str` if a supplied element exposes it |
| diagnostic primary location | a null `Diagnostic.Create` location becomes `Location.None` | encode the observed `None`; do not claim that null was supplied |
| additional-locations and properties containers | null containers become empty collections | encode the observed empty collections; property null and empty values remain distinct |
| C# syntax-tree file path | a null parse-time path becomes `String.Empty` | consume the observable empty path, which remains a malformed required external/unmapped path unless a controlled logical-path or generated hint mapping supplies identity |
| `FileLinePositionSpan` and external `Location.Create` paths | public constructors reject null and preserve empty strings; `HasMappedPath` separately preserves mapped state | use the F5-02 mapped/unmapped discriminator; a mapped empty path remains mapped and carries an empty path value |
| controlled, generated, and external path identities | their former string tokens could collide with scenario-controlled text | retain the redacted token but prefix its canonical field with an explicit path-kind discriminator; a syntax tree cannot hold two identities |
| `SyntaxTree.GetLineVisibility` | exposes `Visible`, `Hidden`, and `BeforeFirstLineDirective` | encode all three with explicit tags; the former Boolean projection is removed |

No other included field collapses null and empty. In particular, nullable diagnostic property values and nullable supplied custom-tag elements retain their existing distinct `str` encodings, duplicate tags and locations remain counted, and a null additional-location element or invalid span is a `CanonicalizationFailure`. Property identity is the declared sorted key/value-entry projection; arbitrary `ImmutableDictionary` comparer objects are not report fields and do not enter canonical identity. Raw title, message format, and arguments are excluded: only the public invariant `GetMessage` result is identity, including the observed empty result after string-overload null-message normalization. Description remains outside V1 identity; adding it would require a new canonical version and Architecture Gate.

Phase 1 canonicalizes only the public `LocationKind.None` and `LocationKind.SourceFile` cases. An unmapped path is `str(token) + byte(kind)`, where kind is `00` controlled, `01` generated, or `02` external. A mapped payload has the same framing but permits only kind `02` external or `03` mapped-empty. Controlled tokens are nonempty manifest logical paths after `/` separator normalization; generated tokens remain `generated:<observableHintName>`; and external tokens remain `external:<lowercase SHA-256 of the strict-UTF-8 Path.GetFullPath result>`. The kind byte prevents a controlled logical path from colliding with the generated/external token vocabulary. The mapped-empty token is exactly an empty string and its separate mapped-payload type prevents it from appearing as an unmapped path; likewise, controlled or generated kinds cannot be mapped payloads. External path characters are neither case-folded nor Unicode-normalized before hashing, the environment is recorded, and an absolute path is never emitted.

Mapped state is a discriminated value: byte `00` means `Unmapped` and has no mapped-path payload; byte `01` means `Mapped` and is followed by the mapped payload, whose first component is the existing non-null `str` encoding. A mapped empty public path therefore encodes `01 + str("") + 03`, distinct from both unmapped and mapped nonempty. A nonempty explicit mapped path is tokenized from its own public `FileLinePositionSpan.Path` and redacted as external; the tree's controlled/generated identity never overwrites it. Line visibility is also one byte: `00` `Visible`, `01` `Hidden`, and `02` `BeforeFirstLineDirective`.

`SourceFile` is `frame(UTF8("source")) + path(unmappedPath) + u64(UTF16SpanStart) + u64(UTF16SpanLength) + mapped(mappedPath) + u64(mappedStartLine) + u64(mappedStartColumn) + u64(mappedEndLine) + u64(mappedEndColumn) + byte(lineVisibility)`. `None` is exactly `frame(UTF8("none"))`. `MetadataFile`, `XmlFile`, `ExternalFile`, and an unknown future kind are valid-but-unsupported public evidence: available metadata module or line information may be reported only as a non-comparison observation, while the whole affected diagnostic snapshot is `Unavailable`/`UnsupportedLocationKind` and the assertion is `UNKNOWN`. An empty required unmapped path, null additional-location element, invalid line span, negative/out-of-range coordinate, failed path resolution, or other malformed location is instead `CanonicalizationFailure` and aggregate `ERROR`.

Input and post-generation compilation diagnostics use the same record format but remain different evidence categories. A generator exception and Roslyn's failure diagnostic are represented separately from normal generator-reported diagnostics.

### Tracked steps

Report step name, graph relationship, output index, reason, and counts in stable ordinal order. Preserve duplicate names and occurrences. Do not serialize or hash arbitrary `Outputs.Value` objects; they may be opaque compiler or user values. Elapsed time is optional observed metadata and never an assertion input. Public JSON properties appear in the report-schema order, map-like entries are arrays sorted by their ordinal keys, and all other arrays use the canonical orders above or explicit run/assertion order. Presentation or generator emission order is never used as identity.

## Required assertions

For a valid completed scenario:

1. `cold-output-determinism`: the cold worker's A source and generator-diagnostic snapshots equal the transition worker's fresh A snapshots.
2. `declared-source-effect`: B's source snapshot changed or stayed unchanged exactly as declared.
3. `declared-diagnostic-effect`: B's generator-diagnostic snapshot changed or stayed unchanged exactly as declared.
4. `declared-invalidation`: for `relevant`, at least one registered final output reason at B is non-`Cached`; for `irrelevant`, every registered final output reason is `Cached`. Zero recorded final-output reasons, a missing output collection, or incomplete final-output evidence is `UNKNOWN`; an empty set can never satisfy the universal `Cached` condition.
5. `restoration`: restored A's source and generator-diagnostic snapshots equal the transition worker's initial A snapshots.
6. `stable-restored-cache`: every registered final output reason on the following unchanged A run is `Cached`. Zero recorded reasons, a missing output collection, or incomplete output evidence is `UNKNOWN`; an empty set can never pass.

`Unchanged` is non-`Cached`: it passes the relevant-invalidation assertion and fails the irrelevant-no-regeneration assertion. Output equality and invalidation are separate assertions, so a relevant mutation may intentionally produce unchanged final output if the scenario says so.

## CLI and exit contract

```text
sourcegen-auditor audit <scenario.json> [--format console|json]
                        [--output <path>] [--timeout <seconds>]
sourcegen-auditor --help
sourcegen-auditor --version
```

Console is the default format. JSON on stdout contains no prose; worker output never shares that stream. `--output` writes the selected rendering to a file. A report-write failure is an execution error even if the audit assertions had completed.

| Exit | Meaning |
| ---: | --- |
| 0 | aggregate `PASS` |
| 1 | aggregate `FAIL` |
| 2 | aggregate `UNKNOWN` |
| 3 | aggregate `ERROR`, including load, timeout, crash, protocol, canonicalization, or report failure |
| 64 | invalid CLI invocation or invalid scenario before execution |
| 130 | user cancellation when root-worker cleanup completes; any best-effort report has verdict `ERROR` and failure kind `Canceled` |

These nonzero values are SourceGen Auditor conventions. Root-worker termination failure overrides cancellation and maps to `InternalFailure`/exit 3. Exit mapping is an adapter and is not stored in the domain verdict.

## Test execution and tool packaging

Phase 1 selects Microsoft Testing Platform v2 exclusively. The checked-in `global.json` pins SDK 10.0.400 and contains `"test": { "runner": "Microsoft.Testing.Platform" }`. `SourceGenAuditor.Tests.csproj` is a `net10.0` executable with `OutputType=Exe`, `IsTestProject=true`, and `IsPackable=false`; its only direct test package is `xunit.v3.mtp-v2` 4.0.0. Do not add `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`, or `TestingPlatformDotnetTestSupport`. VSTest is not selected.

`dotnet test` uses the .NET 10 MTP grammar with `--solution`, no extra `--`, and xUnit's built-in `--report-xunit-trx` writer. The report is written below the explicit results directory and must exist and parse as XML.

Phase 1 is an installable framework-dependent .NET tool, not merely a runnable project. The CLI project locks `PackAsTool=true`, `ToolCommandName=sourcegen-auditor`, `PackageId=SourceGenAuditor.Tool`, and `Version=0.1.0`. Acceptance must pack, install that exact local package to a repository-owned isolated `--tool-path`, invoke `--version` and both scenario audits through the installed shim, uninstall it, and prove the shim was removed. Publishing, signing, self-contained/RID-specific packages, and release automation are deferred.

## Dependencies and redistribution

| Dependency layer | Locked version or constraint | Use | License/redistribution |
| --- | --- | --- | --- |
| .NET SDK/runtime | SDK 10.0.400 / runtime 10.0.11 | active LTS host, C# 14 | .NET product and component terms apply; framework-dependent distribution avoids bundling the runtime |
| `Microsoft.CodeAnalysis.CSharp` | 5.9.0 | C# compilation, generator driver, public evidence | NuGet records MIT; package is built from Roslyn commit `35d9211b841e7613c1d2f8f5af6d628ace696c4c` |
| Roslyn transitive: `Microsoft.CodeAnalysis.Common` | exactly 5.9.0 | supplies the host `Microsoft.CodeAnalysis` assembly and shared compiler/generator abstractions | no direct reference; NuGet records MIT and the package is fixed by the lock graph |
| Direct test package: `xunit.v3.mtp-v2` | exactly 4.0.0 | sole test `PackageReference`; selects the executable MTP v2 stack and xUnit TRX writer | NuGet records Apache-2.0 |
| xUnit transitive family | exact 4.0.0 for `xunit.v3.core.mtp-v2`, `xunit.v3.assert`, `xunit.v3.extensibility.core`, and `xunit.v3.runner.inproc.console`; `xunit.analyzers >= 2.0.0` | test framework implementation and analyzer | no direct references; resolved licenses must be captured from the lock graph |
| MTP v2 transitive family | minimum 2.3.3 for `Microsoft.Testing.Platform`, `Microsoft.Testing.Platform.MSBuild`, `Microsoft.Testing.Extensions.Telemetry`, and `Microsoft.Testing.Extensions.TrxReport.Abstractions` | test host integration and xUnit's TRX abstraction | no direct references; exact resolved versions and licenses come from committed package lock files |

Use target-framework `System.Text.Json`, metadata, cryptography, process, and pipe APIs without separate packages. The MTP and xUnit family rows are transitive constraints reported by the selected `xunit.v3.mtp-v2` 4.0.0 package, not direct references; the checked-in lock graph is authoritative after the first restore. Do not add VSTest packages, a separate TRX extension, Workspaces, MSBuild, a CLI framework, an IPC library, a hashing library, or a report framework in Phase 1. The resolved direct/transitive license inventory and required notices are shipped with the package; package metadata is evidence, not legal advice.

## Test strategy and acceptance

1. Pure unit tests cover manifest validation, exact-content known vectors, length framing, stable order, diagnostic multisets, source diffs, run-reason evaluation, verdict precedence, and exit mapping.
2. Roslyn adapter tests use exactly one configurable test-only `IIncrementalGenerator` fixture for cached, relevant-but-unchanged, changed-output, tracked and untracked intermediate, exception, and partial-output modes.
3. Worker tests use that same fixture for exact named selection, missing dependencies, rejected newer references, timeout, cooperative cancellation, forced termination, process crash, stdout/stderr pollution and truncation, frame/schema/sequence errors, protocol truncation/overflow, report overflow, temporary-file cleanup, and checkpoint preservation.
4. Black-box CLI tests verify console/JSON agreement, clean JSON stdout, exact exits including an OS-delivered Windows console cancellation and best-effort `Canceled` report, invalid manifests, output-write failure, and deterministic report ordering.
5. End-to-end scenarios drive the same single fixture through one relevant and one irrelevant source replacement across the cold and transition worker sequence, including installed-tool worker self-spawn.

Acceptance requires, among other cases: known framing/hash vectors; exact UTF-16 and literal-U+FEFF differences; encoding/preamble metadata excluded from equality; emission-order changes compare equal; diagnostic-order changes and duplicates compare as a counted multiset; invariant messages and redacted location tokens; added/removed/modified hints; absent tracking or unsupported metadata locations as `UNKNOWN`; `Unchanged` not treated as cached; malformed/truncated/oversize/out-of-order frames as `ERROR`; checkpoint deadlines and cleanup; B exceptions/timeouts/crashes preserving prior A evidence; console writes not corrupting JSON; valid MTP TRX; local package install/invocation/uninstall; and no partial run producing `PASS`.

No external provider, Visual Studio, arbitrary project, malicious-generator, lower-Roslyn compatibility range, performance, NuGet.org publishing, or production acceptance is part of Phase 1.

## GO WITH CONDITIONS falsification gates

Phase 1 begins with the checks below. Failure stops work at the indicated boundary; it does not authorize an alternate test runner, protocol, loading path, package form, or evidence rule.

| Check | Measurable pass condition | Failure consequence |
| --- | --- | --- |
| F1 toolchain/test engine | `dotnet --version` is exactly 10.0.400 under `rollForward: disable`; locked restore succeeds; the executable test project exits 0 through the documented `dotnet test --solution` command; its report is a 2010-namespace TRX `TestRun`, counters have `total > 0` and `executed > 0`, and the named `XunitMtpV2ProducesTrx` sentinel result is present with `outcome="Passed"` | Stop and reopen the Architecture Gate for any SDK, runner, package, command, result format, or zero-test condition |
| F2 public Roslyn evidence | For the single fixture's one registered `SourceOutput`, both fresh A runs contain exactly one `New`; relevant B contains exactly one `Modified`, restored A exactly one `Modified`, and stable A exactly one `Cached`; the irrelevant scenario contains exactly one `Cached` at B, restored A, and stable A; every collection is present and nonempty | Stop Phase 1 as `NO-GO`; required public evidence is insufficient |
| F3 driver/cold model | Two fresh fixture processes yield equal canonical A snapshots, while the transition worker retains adjacent driver state and restores A | Stop and reopen the gate before changing process/run topology |
| F4 protocol/failure retention | Every closed V1 schema/state transition round-trips; missing, duplicate, rejected-without-failure, or cross-worker-mismatched admission fails; unknown/duplicate/malformed/truncated/oversize/out-of-order/late data fails; `Partial -> failure` preserves only validated partial evidence; `completed` rejects partial or missing checkpoints; timeout/crash retains the last complete checkpoint; cancellation-plus-cleanup maps to 130, cleanup failure overrides to exit 3; root exits within the two-plus-five-second windows | Stop and reopen the gate before changing IPC, limits, state transitions, or failure/exit semantics |
| F5 canonical contracts | Two fresh processes reproduce every byte string and SHA-256 in ADR 0003 exactly; an independent recomputation agrees; separate tests prove the F5-01 public-observable normalization rules, the three F5-02 mapped-path states and three line-visibility states are pairwise distinct, path identity kinds cannot collide, composed/decomposed Unicode differ, literal U+FEFF differs, equal code units with different encoding/BOM/checksum metadata compare equal, duplicate diagnostics change occurrence count, only None/SourceFile locations canonicalize, emission/diagnostic order is ignored, and no absolute path enters records or reports | Stop and reopen the gate; no hash/schema improvisation |
| F6 packaged tool | `dotnet pack` creates and hashes `SourceGenAuditor.Tool.0.1.0.nupkg`; tool install uses a generated config whose only source is that fresh package directory plus fresh NuGet/Dotnet caches; installed `sourcegen-auditor` reports 0.1.0, self-spawns its worker, completes both scenarios, uninstalls, and leaves no shim | Stop and reopen the gate before adding a feed/dependency workaround or changing distribution/worker launch architecture |
| F7 bounded artifacts | The approved fixture's frames and report remain below 8 MiB/frame, 32 MiB/worker, and 32 MiB/report, with exact overflow failures covered | Stop and reopen the gate before raising limits or changing payload shape |

Lower-than-host Roslyn references are outside fixture-covered compatibility in Phase 1. Their success or typed failure does not falsify the gate and cannot expand advertised support.

## Security and execution boundary

A user-selected generator executes arbitrary managed code with the user's account permissions. It may read or write files, inspect environment state, use the network, spawn processes, emit large output, hang, crash, or terminate its worker. Assembly loading and a worker process provide dependency and fault boundaries only. The worker process is not a security sandbox, and descendant termination is best effort. Phase 1 documents malicious or untrusted generators as unsupported and prohibited inputs; it cannot determine whether code is trustworthy.

## Risks and rejected alternatives

- **In-process execution rejected:** cooperative cancellation cannot stop every generator; a crash can terminate the CLI and destroy partial evidence.
- **Project/MSBuild loading rejected:** imported SDK state, implicit references, analyzers, generated files, and environment properties defeat the narrow controlled-input claim and add unnecessary packages.
- **One process per transition rejected:** driver cache state would be lost between A, B, and restored A.
- **Warm repeat as determinism proof rejected:** a cached callback may not execute.
- **Historical A cache claim rejected:** public/source evidence supports only comparison with the previous driver state.
- **Strict Roslyn equality as a technical requirement rejected:** .NET can satisfy a lower request with an equal-or-higher loaded assembly. Lower-or-equal admission is allowed, but only the executable fixture-covered component closure may be advertised as compatible.
- **Broad Roslyn compatibility rejected for Phase 1:** the single equal-version fixture cannot establish an older-version range; successful opportunistic loading is per-artifact evidence, not a support promise.
- **`SourceText.GetChecksum()` as equality rejected:** it is not a project-defined exact-character comparator.
- **Syntax normalization rejected:** formatting, Unicode, and line endings are observable generator output.
- **Full source text in reports rejected:** hashes and precise deltas suffice for the first slice and reduce accidental disclosure.
- **Security-sandbox wording rejected:** the worker has the user's authority.

Primary risks are unverified lower-version Roslyn loads, dependency-resolution variance, generator side effects outside declared inputs, opaque tracked values, Base64 log disclosure, correlation or guessing of hashed external paths, platform-specific process-tree behavior, orphaned atomic-write files after abrupt parent death, report growth, and Roslyn evidence changes. The host pin, fixture-covered claim boundary, explicit hashes, bounded protocol, typed failures, and falsification gates constrain but do not eliminate them.

## Approved decisions

The owner approved ADRs 0001-0004 and amendments F5-01/F5-02 for Phase 1:

1. compiled DLL plus the narrow source-replacement scenario contract, with no project loading;
2. .NET 10.0, exact SDK selection, Roslyn Common/CSharp 5.9.0.0 host components, closure-wide lower-or-equal admission, newer/unsupported-component rejection, and compatibility claims limited to the single fixture-covered equal-version closure;
3. two sequential workers and the exact two-pipe framing, limits, checkpoint deadlines, output capture, cancellation, cleanup, and atomic-write lifecycle;
4. locked source/diagnostic canonicalization, explicit effect expectations, four-outcome verdict model, path redaction, report limits, CLI, and exit codes;
5. MTP v2 with `xunit.v3.mtp-v2` 4.0.0 and no VSTest packages; an installable `SourceGenAuditor.Tool` 0.1.0 as the Phase 1 deliverable; and the F1-F7 stop/re-gate criteria.

## Official references

Primary sources checked on 2026-09-02:

- [Microsoft.CodeAnalysis.CSharp 5.9.0 package, targets, license, and source commit](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp/5.9.0)
- [Pinned Roslyn commit `35d9211b841e7613c1d2f8f5af6d628ace696c4c`](https://github.com/dotnet/roslyn/commit/35d9211b841e7613c1d2f8f5af6d628ace696c4c)
- [Pinned incremental-generator design](https://github.com/dotnet/roslyn/blob/35d9211b841e7613c1d2f8f5af6d628ace696c4c/docs/features/incremental-generators.md)
- [Pinned `GeneratorDriver` source](https://github.com/dotnet/roslyn/blob/35d9211b841e7613c1d2f8f5af6d628ace696c4c/src/Compilers/Core/Portable/SourceGeneration/GeneratorDriver.cs)
- [Pinned run-result source](https://github.com/dotnet/roslyn/blob/35d9211b841e7613c1d2f8f5af6d628ace696c4c/src/Compilers/Core/Portable/SourceGeneration/RunResults.cs)
- [Pinned state-table source](https://github.com/dotnet/roslyn/blob/35d9211b841e7613c1d2f8f5af6d628ace696c4c/src/Compilers/Core/Portable/SourceGeneration/Nodes/DriverStateTable.cs)
- [Tracked run reasons](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.incrementalsteprunreason?view=roslyn-dotnet-5.0.0)
- [Generator run-result API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.generatorrunresult?view=roslyn-dotnet-5.0.0)
- [Incremental generator inputs](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.incrementalgeneratorinitializationcontext?view=roslyn-dotnet-5.0.0)
- [`WithTrackingName`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.incrementalvalueproviderextensions.withtrackingname?view=roslyn-dotnet-5.0.0)
- [`AnalyzerFileReference` generator discovery](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.diagnostics.analyzerreference.getgeneratorsforalllanguages?view=roslyn-dotnet-5.0.0)
- [`IIncrementalGenerator.AsSourceGenerator`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.incrementalgeneratorextensions.assourcegenerator?view=roslyn-dotnet-5.0.0)
- [`AssemblyLoadContext` versioning, sharing, and type identity](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/understanding-assemblyloadcontext)
- [`Location` public identity surface](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.location?view=roslyn-dotnet-5.0.0)
- [`SourceText.GetChecksum`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.text.sourcetext.getchecksum?view=roslyn-dotnet-5.0.0)
- [`Process.Kill` limitations](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0)
- [.NET 10 support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [.NET 10.0.11 / SDK 10.0.400 release](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.11/10.0.11.md)
- [xUnit.net 4.0 MTP v2 release](https://xunit.net/releases/v3/4.0.0)
- [`xunit.v3.mtp-v2` 4.0.0 package](https://www.nuget.org/packages/xunit.v3.mtp-v2/4.0.0)
- [.NET 10 `dotnet test` MTP mode](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test)
- [xUnit.net MTP command and TRX options](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform)
- [Create and pack a .NET tool](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools-how-to-create)
- [`dotnet tool install`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-install)
- [`dotnet tool uninstall`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-uninstall)
