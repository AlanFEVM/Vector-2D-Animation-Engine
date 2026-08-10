# Release Contract

## Source Authority

- `Directory.Build.props` owns the current source/assembly version.
- `release/release-notes.json` owns bilingual in-app notes; versioned Markdown remains historical documentation.
- English and Simplified Chinese entries keep matching section/item structure and contain no publish placeholders.
- Release Manager refuses to save or publish when source fingerprints changed after load.

## Publish Transaction

- Acquire the release mutex and build in a unique temporary staging directory.
- Accept cooperative cancellation only before the formal output commit; acknowledge it through the established signal contract.
- Verify native and bootstrap versions, embedded notes, payload self-test, filename, output containment, size, and SHA-256.
- Return the version/path/size/hash result contract only after the artifact is committed and rechecked.

## Package Boundary

- Produce exactly `VectorAnimationEngine-<version>-win-x64.exe` in the selected empty output directory.
- Embed the compressed framework-dependent application as `.V2DEngine` payload.
- Refresh external `.V2DEngine` atomically on launch; reuse a valid external `.Runtime` or provision compatible .NET 8 Core and Windows Desktop x64 runtimes with hash verification.
- Keep formal output under `artifacts/release` or an explicitly requested empty directory, never the repository root.
