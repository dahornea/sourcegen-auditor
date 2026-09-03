# SourceGen Auditor

> Audit observed generator behavior under one declared controlled scenario.

SourceGen Auditor is a local-first .NET CLI that audits observed behavior of one Roslyn incremental source generator under one declared controlled scenario. It does not offer a global proof of determinism or optimal incremental caching.

## Status

The Architecture Gate and amendments F5-01/F5-02 are approved. Phase 1 delivers a framework-dependent installable `dotnet tool` package (`SourceGenAuditor.Tool` 0.1.0, command `sourcegen-auditor`) under the mandatory F1-F7 stop gates. Phase 2 remains out of scope.

Start with:

- [Product specification](PRODUCT.md)
- [Architecture and official research](ARCHITECTURE.md)
- [Scenario and report contracts](docs/scenarios.md)
- [Implementation plan](PLAN.md)
- [Architecture decisions](docs/decisions/README.md)
- [Architecture and implementation review record](CODE_REVIEW.md)

## Architecture verification

From the repository root:

```powershell
pwsh -NoProfile -File ./eng/verify.ps1
```

or:

```sh
bash ./eng/verify.sh
```

These scripts validate the approved architecture documents, Phase 1 repository shape, and canonical-vector contracts. They do not run a generator or establish product acceptance.

## Security boundary

A generator selected by the user executes code with the user's permissions and can access local resources available to that user. The worker process limits the effect of hangs and crashes on the CLI; it is not a security sandbox. Do not use SourceGen Auditor with malicious or untrusted generators.

## License

SourceGen Auditor is licensed under the [MIT License](LICENSE).
