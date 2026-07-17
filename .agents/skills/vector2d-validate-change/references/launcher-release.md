# Launcher, Diagnostics, And Release

## Development Launcher

The tracked root `VectorAnimationEngine.exe` is normally a small launcher. It starts the native project with `dotnet watch` and module-scoped Metadata Hot Reload. `--no-hot-reload` uses a plain `dotnet run` session.

Relevant code:

- `launcher/Program.cs`
- `native/App/AppHost.cs`
- `native/App/HotReloadHandler.cs`
- `native/App/HotReloadModules.cs`
- `native/App/SingleInstanceLease.cs`
- `native/App/LauncherShutdownSignal.cs`

The launcher uses `--no-restore`; restore the native project before first launch on a new checkout. Unknown updated root types route conservatively to all modules, but known engine, rendering, timeline, workspace, and inspector types should still be registered for scoped refreshes.

## Logs And Processes

- Launcher sessions write `logs/launcher.log` and root `logs/native-*.log`; `launcher.log` includes captured `dotnet watch` output and rotates once at 8 MB.
- Direct native/benchmark execution may write logs relative to the build output instead of the repository root.
- A second native instance exits through the single-instance lease.
- Force-killing the native app can leave the launcher/watch process running because no shutdown signal was sent.
- For live hot-reload compiler diagnostics, inspect `launcher.log`; a foreground `dotnet watch` terminal remains useful when interactive console behavior is required.

## Rebuild Launcher

```powershell
dotnet publish launcher\VectorAnimationEngine.Launcher.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true -o publish-launcher
Move-Item publish-launcher\VectorAnimationEngine.exe .\VectorAnimationEngine.exe -Force
Remove-Item publish-launcher -Recurse -Force
```

Stop launcher/native processes first. Verify the published file exists before replacing the tracked root EXE.

## Publish Standalone

```powershell
dotnet publish native\VectorAnimationEngine.Native.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true `
  -p:EnableCompressionInSingleFile=true -o publish-native
```

Prefer keeping the artifact in `publish-native` until verification completes. Installing it as the root EXE replaces the development launcher; rebuild the launcher to restore normal development behavior.

Before distribution:

- run all affected regression suites
- launch the standalone EXE on Windows x64
- verify startup, Direct2D/GDI fallback, logs, and shutdown
- record file size and SHA256
- confirm whether the root binary should be committed

There is currently no signing, installer, versioned archive, or multi-runtime release pipeline.
