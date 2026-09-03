# Scenario and report contracts

Status: approved and implemented for Phase 1. JSON property names and reason codes are compatibility contracts.

## Scenario V1

A scenario owns all inputs needed for one audit. Paths are relative to the manifest directory. Every file has an expected lowercase SHA-256 so the tool can reject drift before loading or parsing it. The tool computes and reports the hashes again at execution.

```json
{
  "schemaVersion": 1,
  "id": "comment-is-irrelevant",
  "generator": {
    "assemblyPath": "generator/MyGenerator.dll",
    "sha256": "<64 lowercase hex characters>",
    "typeName": "Example.MyGenerator"
  },
  "baseline": {
    "assemblyName": "SourceGenAuditorScenario",
    "sources": [
      {
        "logicalPath": "Input.cs",
        "path": "inputs/Input.A.cs",
        "sha256": "<64 lowercase hex characters>"
      }
    ],
    "references": [
      {
        "path": "references/System.Runtime.dll",
        "sha256": "<64 lowercase hex characters>"
      }
    ],
    "parseOptions": {
      "languageVersion": "14.0",
      "documentationMode": "parse",
      "preprocessorSymbols": []
    },
    "compilationOptions": {
      "outputKind": "dynamicallyLinkedLibrary",
      "nullableContext": "enable",
      "allowUnsafe": false
    }
  },
  "mutation": {
    "id": "replace-input-comment",
    "kind": "replaceSourceText",
    "targetLogicalPath": "Input.cs",
    "replacementPath": "inputs/Input.B.cs",
    "replacementSha256": "<64 lowercase hex characters>",
    "relevance": "irrelevant",
    "expectations": {
      "generatedSources": "unchanged",
      "generatorDiagnostics": "unchanged"
    }
  }
}
```

Validation is closed-world: unknown properties, missing hashes, paths escaping the scenario directory, duplicate logical paths, duplicate reference identities, unsupported enum values, and multiple mutations are errors. Symbolic links/reparse points that resolve outside the scenario directory are rejected. The public CLI opens the manifest, generator-directory DLLs, sources, references, and replacement with sharing-denying read handles, reloads and hashes the scenario from that leased byte set, and retains the handles across both workers. A mismatch is exit 64, not an audit failure. The lease prevents ordinary pathname replacement or overwrite on the Phase 1 Windows host; it is not a security boundary against arbitrary generator code or pre-existing write-capable handles.

`relevance` is mandatory and is exactly `relevant` or `irrelevant`. It declares expected final-output invalidation; the tool never derives it from syntax or semantics. Both effect expectations are mandatory and independently declare whether the canonical generated-source and generator-diagnostic snapshots at B should be `changed` or `unchanged` from A. No default is inferred.

Phase 1 supports at least one source and one explicit metadata reference, one replacement of an existing source, C# 14 parsing, and the listed compilation options. Additional texts, analyzer config, option/reference mutations, source add/remove, multiple mutations, environment variables, and project loading are schema errors until a later version adds them.

The generator directory is the only private-dependency probe root. Every file-backed managed dependency resolved through the worker's private loader and its hash is recorded. Shared-framework resolution and dynamic loads outside that loader are ambient observations the tool cannot completely inventory. Native dependencies, alternate probe roots, NuGet restore, `.deps.json` policy beyond the worker host, and network acquisition are unsupported in Phase 1.

## Run sequence

The parent runs two sequential workers:

1. `coldA`: fresh process, generator instance, driver, and A compilation;
2. `transitionA`: second fresh process and initial A run;
3. `mutatedB`: the second worker reuses the returned driver and replaces the declared source tree;
4. `restoredA`: it reuses the returned driver and restores the original A tree;
5. `stableA`: it reuses the returned driver with unchanged restored A.

The manifest inputs are identical for the two cold A observations, but the report records runtime, OS, architecture, culture, timezone, Roslyn assembly identity, generator/dependency hashes, and input hashes. These facts bound rather than eliminate ambient variation.

## Report V1

The public JSON report has a stable top-level shape:

```text
schemaVersion: 1
tool: { version, runtime, roslyn }
scenario: { id, manifestHash, generator, controlledInputs, mutation }
compatibility: { roslynReferences: RoslynReferenceDecision[], aggregateAdmissionDecision: Admitted|Rejected|Unavailable, fixtureCoverage }
runs: RunEvidence[]
observations: ObservedFact[]
assertions: AssertionResult[]
verdict: PASS | FAIL | UNKNOWN | ERROR
partialEvidence: boolean
failure: FailureRecord | null
```

Arrays are deterministically ordered unless multiplicity represents separate observations. Every fact and assertion has a stable ID. Assertions cite evidence IDs and use stable reason codes plus human text. The console renderer consumes the domain result, not the serialized report, and cannot change verdicts or exits.

Each `RoslynReferenceDecision` contains `referencingAssemblySha256`, `simpleName`, `requestedVersion`, nullable matching `hostVersion`, and `admissionDecision`; entries are sorted by simple name, numeric four-part requested version, then referencing hash. Any newer supported component or any unsupported `Microsoft.CodeAnalysis*` component rejects the aggregate. A load failure before inspection completes yields an empty list and `Unavailable`. `fixtureCoverage` is `Covered` only for the approved fixture's complete Common/CSharp 5.9.0.0 closure; otherwise it is `NotFixtureCovered`.

`RunEvidence` records completion (`Complete`, `Partial`, or `Unavailable`), checkpoint identity, generated-source records and aggregate hash, categorized diagnostic records and hashes, tracked-step graph/reasons, bounded stdout/stderr metadata, and an optional typed failure. Full generated text is not included.

For diagnostic locations, `UnmappedPathValueV1` is `{ kind: "Controlled" | "Generated" | "External", token }` with a nonempty token. `MappedPathPayloadV1` is `{ kind: "Empty", token: "" }` or `{ kind: "External", token }` with a nonempty external token. `MappedPathV1` is either `{ hasMappedPath: false }`, with no `value`, or `{ hasMappedPath: true, value: MappedPathPayloadV1 }`. A source-file location carries `unmappedPath: UnmappedPathValueV1`, `mappedPath`, coordinates, and `lineVisibility` (`Visible`, `Hidden`, or `BeforeFirstLineDirective`). Separate domain types make `Empty` invalid for an unmapped path and `Controlled` or `Generated` invalid for a mapped payload. These are closed discriminated values: JSON never substitutes a magic string, omits required state, or exposes a raw external path.

Failure kinds are:

- `GeneratorException`
- `LoadFailure`
- `CompatibilityFailure`
- `Timeout`
- `Canceled`
- `WorkerCrash`
- `WorkerProtocolFailure`
- `EvidenceLimitExceeded`
- `CanonicalizationFailure`
- `ReportWriteFailure`
- `InternalFailure`

The report preserves all completed checkpoints. A generator exception may carry partial Roslyn evidence; a timeout or crash may leave the active checkpoint unavailable. Neither can satisfy a required assertion.

## Canonicalization summary

Generated source identity is exact ordinal, case-sensitive `HintName`; duplicates are `ERROR`. Content equality is the exact ordinal UTF-16 code-unit sequence, so whitespace, line endings, normalization form, and literal U+FEFF differ. Encoding name, encoding preamble/BOM behavior, and Roslyn checksum are observations only. Canonical hashes use strict UTF-8 without BOM and the exact `sga-source-v1`/`sga-source-set-v1`, unsigned-64-bit-big-endian framing defined in `ARCHITECTURE.md` and ADR 0003.

Diagnostics use the complete invariant-culture tuple, null-distinguishing strings, one-byte booleans, unsigned-64-bit-big-endian numbers and counts, framed sequences, canonical location bytes, sorted additional locations/properties/tags, and occurrence counts defined in those documents. Under F5-01, values are the public Roslyn 5.9.0 values after Roslyn normalization: `HelpLinkUri` is always its observable non-null string—empty when Roslyn discarded an original null—and no original constructor argument is claimed. The descriptor string overload likewise collapses null and empty title/message-format strings to observable empty `LocalizableString` values; raw title/format are excluded, and only the resulting invariant `GetMessage` enters identity. Null and empty remain distinct for nullable tag elements and property values. Under F5-02, mapped state, controlled/generated/external path identity, and all three line-visibility states use explicit discriminators. Unique records are sorted by unsigned canonical bytes and compared as a counted multiset. Raw absolute paths are replaced with controlled logical values, generated hint tokens, or lowercase-SHA-256 external tokens. Only `None` and `SourceFile` locations canonicalize in Phase 1. Valid metadata, XML, external-file, or future location kinds make the diagnostic snapshot unavailable with `UnsupportedLocationKind` and the affected assertion `UNKNOWN`; malformed paths, coordinates, Unicode, or canonical bytes are `CanonicalizationFailure` and `ERROR`. Neither case is silently collapsed. Arbitrary tracked output values are not serialized.

## Verdict summary

Required assertions and their evidence are defined in `ARCHITECTURE.md`. Aggregate precedence is `ERROR > FAIL > UNKNOWN > PASS`; every required assertion must pass for aggregate `PASS`. Missing completed public tracking evidence is `UNKNOWN`. Execution, loading, protocol, canonicalization, cancellation, or report failures are `ERROR`.
