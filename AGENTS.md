# SourceGen Auditor contributor instructions

## Current phase

Phase 1 was completed at commit `ef11d3167b51ed1fb87514115c93102b43c9234c`, including Architecture Amendments F5-01 and F5-02. The current work is publication polish over those capabilities; Phase 2 is not approved. Read `PRODUCT.md`, `ARCHITECTURE.md`, `PLAN.md`, `docs/scenarios.md`, and ADRs 0001-0004 before changing scope. The historical F1-F7 stop conditions remain authoritative for any future re-execution or architectural change.

## Ownership and evidence

- The primary agent owns decisions, synthesis, and every file change.
- Treat scenario-declared relevance as input. Never infer whether a mutation should matter.
- Missing required evidence is `UNKNOWN` or `ERROR`, never `PASS`.
- Keep observations separate from conclusions, report DTOs separate from domain decisions, console output separate from machine output, and exit-code mapping at the process boundary.
- Do not claim that Roslyn tracking proves semantic correctness or that the tool controls the compiler host or operating system.
- A selected generator executes arbitrary code with the user's permissions. A worker process is fault containment, not a security sandbox.

## Subagents

Use at most two concurrent subagents so the primary plus subagents never exceeds three active threads. Subagents are read-only and limited to the project-scoped roles in `.codex/agents/`: `roslyn_researcher`, `acceptance_designer`, and `phase0_reviewer`. They return evidence to the primary and do not edit files or make final decisions.

## Scope discipline

The completed Phase 1 is limited to C#, one selected `IIncrementalGenerator`, one narrow controlled scenario, one generator fixture, the pinned Roslyn host and explicit admission/evidence policy, an MTP v2 test executable, and one installable `dotnet tool` package with console plus JSON results. Preserve the non-goals and do not expand the bounded product claim or improvise a different architecture.

## Verification

Run one of the following from the repository root:

```powershell
pwsh -NoProfile -File ./eng/verify.ps1
```

```sh
bash ./eng/verify.sh
```

Phase 0 verification checks documentation and repository shape only. It is not product acceptance.
