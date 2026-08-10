---
name: vector2d-release-workflow
description: Implement and review Vector 2D Animation Engine release work involving semantic version metadata, bilingual in-app release notes, Release Manager UI/data handling, source fingerprint checks, formal publishing scripts, the distribution launcher, embedded .V2DEngine deployment, external .Runtime provisioning, release cancellation, and single-EXE packaging.
---

# Vector2D Release Workflow

Preserve one authoritative version/release-note source and one atomic formal package boundary.

## Route The Task

- Read [module-map.md](references/module-map.md) for version, manifest, manager, script, bootstrap, and documentation owners.
- Read [release-contract.md](references/release-contract.md) before changing save, publish, cancellation, output, payload, runtime, or validation behavior.
- Also use `$vector2d-winforms-ui` for Release Manager or in-app release-note presentation.
- Use `$vector2d-app-lifecycle` only for the source development launcher; it is not a formal release artifact.

## Workflow

1. Record the requested version, release-note entry, source fingerprints, output directory, and cancellation boundary.
2. Keep `Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`, and the selected manifest entry synchronized.
3. Validate bilingual structure, visibility, dates, placeholders, and source freshness before publishing.
4. Build into disposable staging; validate payload, metadata, filename, size, and hash before committing output.
5. Preserve the last successful artifact until the new EXE passes all checks.
6. Run `$vector2d-validate-change` with `Release`; add affected native suites before formal distribution.
7. Update `README.md`, `docs/RELEASE_MANAGER.md`, and versioned release notes when their contracts change.

## Invariants

- Formal output contains exactly one replaceable EXE smaller than 5 MiB.
- Never overwrite the repository-root development `VectorAnimationEngine.exe` with a formal package.
- The EXE embeds the complete compressed `.V2DEngine`; `.Runtime`, logs, projects, and user data remain external.
- Source fingerprint mismatch, placeholder notes, invalid output scope, or cancellation before commit aborts publishing.
- Final output replacement is atomic; temporary payload, runtime, and validation files do not enter the source diff.
