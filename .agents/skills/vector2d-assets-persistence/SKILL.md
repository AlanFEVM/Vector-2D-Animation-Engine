---
name: vector2d-assets-persistence
description: Implement and review Vector 2D Animation Engine project storage and asset workflows involving Save/Open, .v2dProject manifests, .Vault and .TimeLine files, journal recovery, checksums, project folders, Vault symbols, asset tags/search, SVG import/export/drop, Break Apart, path validation, and backward-compatible document decoding.
---

# Vector2D Assets And Persistence

Treat project I/O as a validated transaction over a managed directory, and keep asset identity stable across model, files, and UI.

## Route The Task

- Read [module-map.md](references/module-map.md) to locate storage, asset, SVG, UI, and regression owners.
- Read [persistence-contract.md](references/persistence-contract.md) for manifest, checksum, path, journal, rollback, or compatibility work.
- Also use `$vector2d-project-model` for in-memory ownership, duplication, nested references, or composition contracts.
- Also use `$vector2d-drawing-geometry` when SVG conversion changes local geometry or topology.
- Also use `$vector2d-winforms-ui` for Vault panels, search/tag controls, dialogs, or previews.

## Workflow

1. Define the stable project, scene, symbol, layer, instance, folder, and tag IDs crossed by the change.
2. Validate schema, IDs, references, paths, limits, and checksums before replacing the live project.
3. Write managed files to staging, flush durable content, record recovery state, and commit in the established order.
4. Keep legacy read compatibility explicit; do not silently reinterpret malformed current data.
5. Rebind the active project, Vault, Stage, timeline, and selection only after a complete successful load.
6. Extend the asset or project-composition regression partial.
7. Run `$vector2d-validate-change` with `Timeline`; add `Freehand` for SVG geometry and `Render` for previews.
8. Update `docs/USER_GUIDE.md` when file layout or asset interaction changes.

## Invariants

- Never trust manifest paths, stable IDs, hashes, or object arrays before validation.
- Reject traversal, reparse-point escape, duplicate IDs, cycles, oversized files, and cross-project references.
- Failed save/open/recovery must leave the last valid project or a recoverable journal state.
- SVG import stays one whole object until Break Apart explicitly materializes supported local geometry.
- Asset tag/folder ordering and assignment survive save/load without relying on UI indices.
