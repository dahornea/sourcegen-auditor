# Phase 0 owner review

Status: **Reopened Architecture Gate reviewed; READY for owner decision**

An independent read-only reviewer inspected the completed Phase 0 diff against the owner's request, official evidence, and internal contracts. The review was bounded to contradictions, unsupported claims, missing failure states, unnecessary scope, and verifier defects. No files were edited by the reviewer.

## Findings and disposition

| Priority | Finding | Disposition |
| --- | --- | --- |
| P1 | PowerShell verification required a security-boundary phrase not present verbatim. | Corrected the architecture wording to state directly that the worker process is not a security sandbox. |
| P1 | `AnalyzerFileReference` returns `ISourceGenerator` wrappers and cannot by itself select the manifest's original incremental implementation type. | Replaced that selection path with pre-load reference inspection, exact named-type resolution in the worker loader, `IIncrementalGenerator` validation, and public `AsSourceGenerator` conversion. Phase 1 is now limited to one configurable fixture type. |
| P1 | “Every output is cached” could pass vacuously when no final-output reasons were recorded. | Defined zero, missing, or incomplete final-output reasons as `UNKNOWN`; an empty set cannot pass. |
| P1 | Extension denylists could falsely certify product code or packages placed outside `src` and `tests`. | Replaced both verifiers' shape logic with an exact Phase 0 file allowlist. |
| P2 | Recording “every resolved managed dependency” overstated what a private loader can observe. | Limited the guarantee to file-backed dependencies resolved by the worker's private loader and identified shared/dynamic loads as ambient. |
| P2 | Automatic refusal of “untrusted” code was not machine-verifiable. | Recast it as a documented unsupported/prohibited input boundary and explicitly said the tool cannot determine trust. |

The reviewer's pre-correction verdict was **NOT READY**. All four blockers and both additional findings were accepted. A focused re-review confirmed five fixes and caught the same empty-set condition in the stable-restored-cache assertion; the primary applied the shared zero-record rule there as well. No unrelated or correction-introduced blocker was reported.

## Historical verification record before the gate reopened

These checks describe the earlier 22-file draft and are retained only as history; they are not evidence for the reopened gate:

- `pwsh -NoProfile -File ./eng/verify.ps1` — PASS; 22 required files and the exact Phase 0 allowlist.
- `bash -n ./eng/verify.sh` — PASS from Git Bash; shell syntax accepted.
- `bash ./eng/verify.sh` — PASS from Git Bash; 22 required files and the exact Phase 0 allowlist.

The shell checks were run from Git Bash at the repository root. Phase 0 verification checks repository shape and specification markers only; it is not generator or product acceptance. A fresh 23-file record is required below before approval is requested.

## Reopened Architecture Gate review

The renewed review must verify all seven owner blockers as one coherent specification: MTP v2 only; lower-or-equal Roslyn admission without an unsupported compatibility promise; an installable local `dotnet tool`; locked source and diagnostic comparison contracts; a closed bounded worker protocol; measurable F1-F7 stop/re-gate criteria; and a public claim limited to observed behavior under one declared controlled scenario. It must also confirm that only one configurable generator fixture is proposed and that Phase 0 still contains no product code, project, fixture, package, test, or CI artifact.

The configured read-only reviewer initially returned `NOT READY` and identified exact SDK roll-forward, closure-wide Roslyn admission, local-package provenance, canonical diagnostic/location definitions and vectors, protocol-schema/state-machine closure, falsification measurability, and stale-verification defects. Each finding was accepted and corrected in the specifications and proposed ADRs without adding implementation artifacts.

The final full re-review returned **READY — no concrete blockers remain**. It confirmed all seven owner blockers, the one-fixture limit, public/security claim boundaries, exact mapped allowlist, verifier syntax, diff whitespace, and independent recomputation of every published normative SHA-256 vector. The reviewer was read-only and edited no files.

## Fresh reopened-gate verification

Final checks run from the repository root on 2026-09-03 after restoring the unchanged `.codex` directory:

- `pwsh -NoProfile -File ./eng/verify.ps1` — PASS; 23 required files and the exact Phase 0 allowlist.
- `bash -n ./eng/verify.sh` — PASS from Git Bash; shell syntax accepted.
- `bash ./eng/verify.sh` — PASS from Git Bash; 23 required files and the exact Phase 0 allowlist.

The first fresh execution found one stale verifier phrase left from the pre-reopen plan. Both verifier scripts were corrected to require `renewed explicit approval`, and every command above then passed. No Phase 1 product, project, fixture, package, executable test, or CI artifact was created or run.

## F5-01 and F5-02 amendment review

The configured read-only reviewer examined the owner-approved diagnostic-canonicalization amendments after implementation. Its initial F5-02 verdict was **NOT READY** for two blockers: the binary model permitted invalid unmapped/mapped path-kind combinations, and no JSON/report projection proved the required mapped-state distinction. A focused pass also identified an inaccurate title/message-format sentence and the missing `LocationV1` `None` report variant.

The primary accepted every finding. The binary domain now uses separate closed `UnmappedPathValue` and `MappedPathPayload` types with canonical token validation; `CanonicalSourceLocation` rejects null and undefined state; the report projection uses separate `UnmappedPathValueV1` and `MappedPathPayloadV1` shapes; `MappedPathV1` omits `value` only when unmapped; and `LocationV1` has exact `None` and `SourceFile` variants. Tests lock unmapped, mapped-empty, and mapped-nonempty binary and JSON inequality, omission/presence behavior, path redaction, and malformed factory rejection. The documentation now states that title, raw message format, and arguments are excluded and only invariant-culture `GetMessage` enters identity.

The final focused re-review returned **READY** with no remaining amendment blocker. The reviewer was read-only and edited no files. This verdict covers the amendment only; the configured reviewer must run again after complete Phase 1 implementation.

## Phase 1 implementation review

Status: **READY — no concrete Phase 1 blockers remain**

The configured read-only reviewer inspected the complete implementation, governing contracts, fresh feasibility evidence, installed-package acceptance, and restored repository state. The reviewer made no file changes and returned the final verdict: “READY — no concrete Phase 1 blockers remain. The implementation, current F5 evidence, package acceptance, and restored repository state conform to the approved Architecture Gate and ADRs.”

### Findings and dispositions

| Priority | Finding | Disposition |
| --- | --- | --- |
| High | The earlier passing F5 pair predated later canonicalizer hardening. | Preserved every prior failure and produced the definitive current-code pair under `artifacts/feasibility/F5-02/fresh-final-current-75092f9e26fe40d5bd1f8413d53f7aff`: 38/38 in each fresh process, identical sorted outcome-vector SHA-256 `6a32afcc96d5ab00acf1e8048b3f8a47388fdd46ae14d1382adfee93edc0d39b`, independent vector recomputation `PASS`. |
| High | Windows handle-inheritance clearing ignored native failures. | Every evidence/control/stdout/stderr handle is validated and every native clear must succeed before scenario or generator code runs; failure emits typed `InternalFailure`. Unit and descendant-lifecycle tests cover the boundary. |
| Medium | Public exit 130 was proven only at the supervisor boundary. | Added a black-box Windows process-group test that delivers an OS console-break event to the public CLI and requires an `ERROR`/`Canceled` report plus exit 130. |
| Medium | Roslyn admission was not proven across a private dependency closure. | Added metadata-only target-to-private-dependency probes for lower, equal, newer, and unsupported Roslyn references, with exact referencing-assembly-hash attribution and pre-load rejection. |
| Medium | Missing tracked-step evidence had no explicit evaluator acceptance test. | Added mutated-B and stable-A cases requiring `TRACKING_UNAVAILABLE`, aggregate `UNKNOWN`, and exit 2 without adding another generator fixture. |
| Medium | Invariant diagnostic-message behavior was implemented but not demonstrated across cultures. | Added a culture-sensitive numeric diagnostic and proved identical invariant canonical bytes under `fr-FR` and `de-DE`. |
| Medium | A second hidden `__pipe-holder` production command existed only for a lifecycle test. | Removed the shipped hook; the single fixture now uses the host's ordinary `ping`/`sleep` process for the bounded descendant test. The fixture's configuration-independent SHA-256 is locked everywhere as `0f22ceda1bb8d75701a962c325b68f9dc0fd202018bea4e0f170a48b88da3fa1`. |
| Medium | The packaged notice inventory named Roslyn's license but omitted its redistributed notice text. | Reproduced the Common/CSharp 5.9.0 package notice, and made package acceptance require its copyright and permission clauses. |

### Final reviewed evidence

- Phase 0 verifier: `PASS`, 30 required files, approved Phase 1 shape, independent normative vectors.
- Release build: `PASS`, zero warnings and zero errors.
- MTP v2/TRX: 106 total, 106 executed, 106 passed, named sentinel passed.
- F5 current-code pair: 38/38 plus 38/38, identical outcome vectors, independent recomputation passed.
- Package: `SourceGenAuditor.Tool.0.1.0.nupkg`, SHA-256 `4d0b646759fe29cdfb04a4819beec1e93cb9b05f0c59c72544189fbd35a8565e`, 5,750,858 bytes, required README/Roslyn notice present, repository/commit metadata absent.
- Installed-tool smoke: version 0.1.0, relevant scenario `PASS` 6/6, irrelevant scenario `PASS` 6/6, uninstall removed the shim.
- Repository state: `.codex/` restored, `.codex-phase1-tmp/` absent, exactly four projects, no CI workflow, and `git diff --check` passed with only Git's existing LF/CRLF warning for `README.md`.

## P1-R1 fixture reproducibility correction

The first fresh post-commit build exposed that the pre-commit fixture SHA-256 `0f22ceda1bb8d75701a962c325b68f9dc0fd202018bea4e0f170a48b88da3fa1` included the then-current Git revision in `AssemblyInformationalVersion`. P1-R1 applies the SDK's fixture-scoped `IncludeSourceRevisionInInformationalVersion=false`, requires the stable informational version `1.0.0` during scenario preparation, and supersedes that pre-commit artifact with SHA-256 `fbd57d6aad6771e1035f264f4b5870c0efab278a29ac0dbdffc82a522c433164`. Canonicalization, F5 vectors, generator behavior, public assembly identity, report semantics, and product scope are unchanged.
