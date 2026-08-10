# Launcher And Release Validation

Implementation contracts live in `$vector2d-app-lifecycle` and `$vector2d-release-workflow`; this reference defines acceptance only.

## Development Launcher

The `Launcher` suite must:

- build `launcher/VectorAnimationEngine.Launcher.csproj`
- run its `--validate-development-launcher` self-check
- preserve the repository-root EXE as the source-development launcher only

For manual lifecycle work, confirm dotnet-watch startup, bounded restart behavior, log output/rotation, hot-reload routing, restart-state handoff, duplicate-instance behavior, and clean shutdown.

## Release Manager And Formal Package

The `Release` suite must:

- build `release-manager/VectorAnimationEngine.ReleaseManager.csproj`
- publish into a disposable validation directory through `scripts/publish-single-exe.ps1`
- produce exactly one `VectorAnimationEngine-<version>-win-x64.exe` below 5 MiB
- run `--validate-single-exe` and remove disposable output afterward

For release workflow changes, also validate version/release-note consistency, bilingual manifest structure, source fingerprint mismatch rejection, cooperative cancellation before commit, output containment, result JSON/hash, embedded `.V2DEngine`, and external `.Runtime` reuse/provisioning as affected.

Never run release validation concurrently with builds, benchmarks, visual capture, or another publish. Never copy formal output onto the repository-root development launcher.
