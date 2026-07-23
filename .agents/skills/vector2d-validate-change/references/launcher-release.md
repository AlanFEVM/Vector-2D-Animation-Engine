# Launcher, Diagnostics, And Release

## Development Launcher

Start the source launcher with:

```powershell
dotnet run --project launcher\VectorAnimationEngine.Launcher.csproj
```

It locates the repository root, starts the native project with `dotnet watch`, and enables module-scoped Metadata Hot Reload. Pass `-- --no-hot-reload` to use a plain `dotnet run` session.

Relevant code:

- `launcher/Program.cs`
- `native/App/AppHost.cs`
- `native/App/HotReloadHandler.cs`
- `native/App/HotReloadModules.cs`
- `native/App/SingleInstanceLease.cs`
- `native/App/LauncherShutdownSignal.cs`

The launcher lets the first native build restore dependencies automatically. Unknown updated root types route conservatively to all modules, but known engine, rendering, timeline, workspace, and inspector types should still be registered for scoped refreshes.

## Logs And Processes

- Launcher sessions write `logs/launcher.log` and root `logs/native-*.log`; `launcher.log` includes captured `dotnet watch` output and rotates once at 8 MB.
- Direct native/benchmark execution may write logs relative to the build output instead of the repository root.
- A second native instance exits through the single-instance lease.
- Force-killing the native app can leave the launcher/watch process running because no shutdown signal was sent.
- For live hot-reload compiler diagnostics, inspect `launcher.log`; a foreground `dotnet watch` terminal remains useful when interactive console behavior is required.

## Publish Releases

```powershell
# Normal release: patch ZIP plus SHA-256 only
scripts\publish-split-package.ps1

# New runtime/launcher/layout baseline: full package plus patch
scripts\publish-split-package.ps1 -FullPackage
```

Normal releases replace the complete `.V2DEngine` directory and preserve the installed root launcher and `.Runtime`. Use full-package mode only when those stable components or the layout must change. Publishing retains only ZIP and SHA-256 outputs; expanded package trees are temporary.

Before distribution:

- run all affected regression suites
- validate that patch ZIP roots contain only `.V2DEngine`
- for a full baseline, launch the packaged root EXE on Windows x64
- verify startup, Direct2D/GDI fallback, logs, and shutdown
- record file size and SHA256
- verify that patch mode did not emit a root launcher, `.Runtime`, or full-package archive

There is currently no signing, installer, or external release-upload integration.
