# Launcher, Diagnostics, And Release

## Development Launcher

Build or refresh the repository-root development launcher, then start it with:

```powershell
scripts\publish-development-launcher.ps1
.\VectorAnimationEngine.exe
```

The root `VectorAnimationEngine.exe` is exclusively a local source-development entry point and is ignored by git. It locates the repository root, starts the native project with `dotnet watch`, and enables module-scoped Metadata Hot Reload. Pass `--no-hot-reload` to use a plain `dotnet run` session. `dotnet run --project launcher\VectorAnimationEngine.Launcher.csproj` remains the equivalent command.

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
# Every release: one replaceable EXE only
scripts\publish-single-exe.ps1
```

The command publishes the framework-dependent application, embeds its compressed payload into a small `net472` bootstrap, and retains exactly one EXE smaller than 5 MiB. Replacing the EXE is the complete update operation. On every launch it atomically regenerates the local `.V2DEngine` directory from the embedded payload, then reuses a valid `.Runtime` or downloads one when needed.

Formal release output belongs only in `artifacts\release` or an explicitly requested empty release directory. Never copy or rename it over the repository-root `VectorAnimationEngine.exe`; rebuild that development launcher with `scripts\publish-development-launcher.ps1` instead.

Before distribution:

- run all affected regression suites
- verify the release directory contains exactly one EXE and no ZIP, checksum sidecar, `.V2DEngine`, or `.Runtime`
- verify the EXE is smaller than 5 MiB and its embedded payload passes `--validate-single-exe`
- test bootstrap runtime provisioning in a disposable writable directory with network access
- verify repeated `--deploy-embedded-application` runs regenerate `.V2DEngine` while preserving a valid `.Runtime`
- verify startup, Direct2D/GDI fallback, logs, and shutdown
- record file size and SHA256
- verify `.Runtime`, `.V2DEngine`, logs, and user data remain external to the EXE after launch

There is currently no signing, installer, or external release-upload integration.
