# App Lifecycle Contract

## Process Ownership

- The source launcher owns `dotnet watch` or `dotnet run`; the native process owns the editor window and project state.
- Use bounded waits and explicit ready/shutdown/restart signals. Do not infer success only from process creation.
- Bound unexpected watch restarts and keep process-tree cleanup scoped to the launched child.

## Restart State

- Treat restart tokens as untrusted input: validate shape, age, size, path containment, and reparse points.
- Prepare consumption before constructing the new UI; complete consumption only after the replacement window is ready.
- Keep temporary restart snapshots separate from durable project persistence and tolerate missing optional UI state.

## Hot Reload

- Map known root types to the smallest refresh module; unknown types fall back conservatively.
- Structural changes that cannot be safely applied request a full editor restart.
- Marshal callbacks onto the UI thread and avoid re-entering an active reload batch.

## Diagnostics

- Preserve launcher/native log locations and rotation contracts documented in the repository.
- Log the first meaningful build, watch, startup, or unhandled exception before showing fallback UI or exiting.
