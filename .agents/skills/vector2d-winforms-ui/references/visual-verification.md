# Visual Verification

## Ordinary Controls

Build first, then capture a parameterless internal panel/control:

```powershell
dotnet build native\VectorAnimationEngine.Native.csproj -c Release --no-restore
pwsh -STA -File .agents\skills\vector2d-winforms-ui\scripts\capture-control.ps1 `
  -TypeName MaterialEditorPanel -Width 296 -Height 524 `
  -OutputPath artifacts\ui-verification\material-editor.png
```

The script loads the Release assembly, creates the internal control through reflection, hosts it in an off-screen form, performs layout, and calls `DrawToBitmap`.

Use PowerShell 7 on .NET 8 (`pwsh`), not Windows PowerShell 5 (`powershell.exe`); the latter cannot reliably load this `net8.0-windows` assembly.

Capture at least:

- the normal intended width/height
- the narrowest supported width
- selected/active and disabled states when relevant
- longest realistic labels or values

Inspect images for clipping, overlap, blank regions, low contrast, inconsistent spacing, text overflow, and unexpected layout shifts.

## Bound Panels

For controls that require project/scene data, write a small task-specific STA harness beside the verification artifact or invoke binding methods through reflection after constructing `VectorProject`/`VectorScene`. Reuse the hosting sequence from `capture-control.ps1`; do not add test-only hooks to production controls solely for screenshots.

## Stage Exception

`StageControl` and Direct2D require a real HWND and may not appear in `DrawToBitmap`. Verify them with a shown off-screen form plus `Application.DoEvents`, then inspect:

- `LastFrameUsedDirect2D`
- `LastStats`
- nonblank captured client pixels when the chosen capture method includes HWND output
- GDI fallback behavior when Direct2D is unavailable

Use `$vector2d-stage-workflow` for renderer ordering and cache rules.

## Hot Reload

New root UI types must be mapped by name in `native/App/HotReloadModules.cs`. Unknown types fall back to `Shell`, which may repaint but not rebind the correct workspace or inspector state.

After routing changes, confirm `MainForm.ReloadModulesForHotReload` preserves the open project, active object, workspace, frame, selection, and camera while refreshing only the required module.
