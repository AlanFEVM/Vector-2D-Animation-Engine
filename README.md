# Vector 2D Animation Engine

Native Windows desktop prototype for a large-scale 2D vector animation engine. The workbench combines Adobe Animate-style drawing and timeline workflows with a dense WinForms desktop UI and Direct2D rendering.

## Documentation

- [Current User Guide](docs/USER_GUIDE.md): product workflows, shortcuts, behavior, troubleshooting, and limitations.
- [Release Manager](docs/RELEASE_MANAGER.md): version creation, bilingual release-note management, visibility, and formal publishing.
- [Documentation Index](docs/README.md): current documentation and versioned release history.
- [Agent Guide](AGENTS.md): repository workflow and validation rules.

The user guide describes the current source tree. Versioned release notes describe only the corresponding formal package.

## Development Run

Source development requires the stable .NET 8 SDK selected by `global.json`.

```powershell
scripts\publish-development-launcher.ps1
.\VectorAnimationEngine.exe
```

The repository-root `VectorAnimationEngine.exe` is exclusively the ignored development launcher. It starts non-interactive `dotnet watch` with module hot reload, restores the first build when needed, and preserves the open project, selection, frame, and view across supported workbench reloads. Use the in-app restart button for unsupported structural edits.

Equivalent source commands:

```powershell
dotnet run --project launcher\VectorAnimationEngine.Launcher.csproj
.\VectorAnimationEngine.exe --no-hot-reload
```

Install the pinned SDK when it is missing:

```powershell
winget install --id Microsoft.DotNet.SDK.8 --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
```

The app starts with an empty project, one Symbol, and one visible layer. Stress content is generated only through `Run Stress Scene`.

Codex and other MCP clients can connect to the running editor after enabling **Settings → Codex / MCP**. The default Streamable HTTP address is `http://127.0.0.1:43521/mcp/`; state, tools, settings, playback and project commands are discoverable through MCP. Write access is a separate setting. See the [connection instructions and tool catalogue](docs/USER_GUIDE.md#codex--mcp-接口).

## Product Overview

### Workspaces And Content Model

- `Basic Drawing` edits reusable Symbols, drawing layers, Cels, vector geometry, text, materials, and nested Symbol instances.
- `Scene & Animation` assembles Symbol instances without copying source geometry and includes animation timing controls for FPS, loop state, playback range, and keyframe workflows.
- Symbol and scene timelines use stable layer tracks. A Symbol layer can contain local Cel geometry and nested instances; a scene layer can contain multiple scene instances.

### Drawing And Editing

- Shape, Line, Pen, Simple Pen, Pencil, Brush, Pressure Brush, Mixing Brush, Text, Fill, Ink Bottle, Eyedropper, Gradient, and Eraser tools share the Stage interaction and undo model.
- Pencil produces editable open Bezier strokes with `0-100%` smoothing. Fill and stroke topology supports intersections, partial marquee materialization, editable anchors, and connected boundary updates.
- Mixing Brush computes Optical or Pigment color in real time and commits a baked vertex-color triangle region. Repeated topmost regions on the same layer and Cel can merge; hit testing, marquee selection, transforms, deletion, and Fill erasing operate on actual painted connected islands. Legacy trajectory payloads remain load/render compatible.
- Free Transform supports corner and edge scaling, four-corner rotation, edge skew, and a movable focus. The `Use Shift for proportional scaling` operation preference in Settings chooses the default scaling mode: on (the default) scales freely and holding `Shift` preserves the aspect ratio; off scales proportionally and holding `Shift` allows free scaling. Corner and edge handles use the same rule, and Shift temporarily reverses the mode during a drag.
- Stage `Ctrl+A` selects only current-frame visible, unlocked content. Basic Drawing prioritizes local objects and falls back to visible nested instances; Scene & Animation selects visible editable scene instances.

### Layers, Materials, And Timeline

- Drawing layers support folders, masks, visibility, locking, Outline, color identification, opacity, and 27 blend modes. Masks clip before isolated folder or scene-layer blending.
- The combined layer/timeline strip supports populated and blank keys, held exposures, rectangular frame selection, layer reordering, onion skin, Cel clipboard operations, and Adobe Animate-style F5/F6/F7 commands.
- Classic tween supports either one compatible local vector object at each endpoint or exactly one nested Symbol instance on an otherwise geometry-free Symbol layer. Shape tween supports local vector content only and may match different endpoint object counts.
- Tween spans support removal, endpoint-driven recalculation, solid and gradient color interpolation, and a monotone easing-curve editor with animated anchor add, move, and delete feedback.
- Auto Key materializes the current frame before edits when enabled. When disabled, edits target the held exposure source rather than inserting an implicit key at the playhead.

### Scenes, Assets, And Persistence

- Symbols can recursively instance other Symbols with project-level self-reference and cycle rejection. Scene and underlay composition retain root provenance for selection and editor navigation.
- Vault provides search, tag filtering, real-geometry hover previews, contextual tag assignment, and drag placement. Assigned tags appear as colored trailing dots; hovering shows all assigned tag names.
- `Import SVG...` or Stage file drop imports one SVG as a single whole object. `Break Apart` converts supported SVG content, text, or nested instances into local vector geometry when conversion is valid.
- Projects are managed directories containing one `.v2dProject` manifest plus `.Vault` and `.TimeLine` data. Save/Open validate ownership, paths, IDs, references, limits, and SHA-256 checksums before replacing the live project; journal recovery handles interrupted saves.

### Rendering And Scale

- WinForms owns the desktop shell. The Stage uses Direct2D hardware rendering with a GDI fallback, render-order-aware selection overlays, low-zoom LOD summaries, and cached composition previews.
- The fixed unit model is `1 px = 25 vu` and `1 pt = 0.5 px = 12.5 vu`.
- The target workload is 1,000+ layers, 100,000+ local vector objects, and 100,000,000+ virtual primitives represented by complexity metadata.

## Architecture

```text
native/App/              Application host, settings, diagnostics, hot reload, and regression harness.
native/Engine/           Project model, timeline, vector geometry, composition, and persistence.
native/Rendering/        Stage control plus Direct2D/GDI rendering.
native/UI/               WinForms workbench and reusable controls.
launcher/                Source development and hot-reload coordinator.
distribution-launcher/   Stable single-EXE bootstrap.
release-manager/         Themed source release and release-note management tool.
scripts/                 Development launcher and release packaging entry points.
.agents/                 Repository-specific Agent and validation workflows.
```

The model stores scene data in packed arrays, uses spatial indices and deterministic parallel batches for large workloads, and retains complex vector topology on the CPU. Direct2D caches and rasterizes stable content while interaction overlays remain independently updatable. If Direct2D initialization fails, the Stage continues through GDI.

UI components can be composed from source using flat section lists in
`MainForm.UiComposition.cs`. See [UI composition](docs/UI_COMPOSITION.md) for the
layout API, component state/event contracts, and examples for AI-assisted changes.

## Validation

Use the repository validation entry point so builds and benchmarks share the cross-process validation lock:

```powershell
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Build
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Timeline
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Freehand
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Render
.agents\skills\vector2d-validate-change\scripts\invoke-validation.ps1 -Suite Stress -EnforcePerformanceBudget
```

The repository has no separate test project or CI pipeline. `native/App/Benchmark*.cs` supplies the focused CLI regression harness.

Shared assertions, fixture cleanup, reflection lookup and sampling live in `Benchmark.RegressionHelpers.cs`; feature partials retain their test cases and measurement loops. Use the [benchmark map](.agents/skills/vector2d-validate-change/references/benchmark-map.md) to select suites. `--bench-collision` is a manual project-specific probe, not part of the standard validation suites.

## Release Packaging

Every formal Windows release is one replaceable EXE:

```powershell
scripts\run-release-manager.ps1
scripts\publish-single-exe.ps1
```

Use the themed Release Manager for normal version creation, bilingual release-note editing, in-app visibility, preview, and publishing. The PowerShell command remains the non-interactive entry and rejects version metadata or release-note mismatches. See [Release Manager](docs/RELEASE_MANAGER.md).

```text
VectorAnimationEngine-<version>-win-x64.exe
```

Formal output belongs only under `artifacts\release` or an explicitly supplied empty release directory. Never copy a release package over the repository-root development launcher.

The formal EXE embeds the complete compressed `.V2DEngine` payload and must remain smaller than 5 MiB. On launch, the bootstrap atomically refreshes the external `.V2DEngine` directory. A valid external `.Runtime` is reused; otherwise the bootstrap downloads the matching Microsoft .NET 8 Core and Windows Desktop x64 runtimes, verifies their SHA-512 hashes, and installs them atomically. Replacing the EXE is the update boundary; `.Runtime`, logs, projects, and user data remain external.

## Logs

- Development launcher: `logs/launcher.log`, rotated to `logs/launcher.previous.log` at 8 MiB.
- Native application: `logs/native-yyyyMMdd-HHmmss-pidN.log`.
- Formal bootstrap: `logs/bootstrap.log` beside the packaged application.

The logs capture startup, hot-reload build diagnostics, runtime acquisition, Direct2D fallback, unhandled UI exceptions, and process failures.

## Repository Workflow

Keep completed features and fixes in local git history with concise commits. Agent-assisted work follows [AGENTS.md](AGENTS.md): preserve pre-existing changes, assign one writer to shared hotspots, serialize validation, and keep generated runtime or release artifacts out of source diffs.
