# Persistence Contract

## Managed Layout

A project is a dedicated directory with one `.v2dProject` manifest plus managed `.Vault` and `.TimeLine` content. The manifest is authoritative for ownership and expected relative paths.

## Validation Boundary

- Normalize and contain every path under the project root.
- Reject reparse points on managed paths, unsafe IDs, duplicate IDs, missing references, cycles, invalid array shapes, and files above their limits.
- Verify SHA-256 before parsing or adopting managed content.
- Validate timeline targets, Cel ownership, masks, transforms, and object arrays before replacing the live model.

## Save And Recovery

- Stage all managed outputs before commit and flush content that participates in recovery.
- Keep the journal schema, operation ID, project ownership, backup paths, and commit order mutually consistent.
- Recovery must be idempotent: repeated startup either completes or restores the last known valid layout.
- Best-effort cleanup is acceptable only after the valid state is secured.

## Compatibility

- Deep-copy mutable arrays and contours when constructing snapshots or decoded models.
- Supply intentional defaults for older optional fields; reject structurally invalid current fields.
- Preserve whole-object SVG source until Break Apart succeeds transactionally.
