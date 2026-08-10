# App Lifecycle Module Map

| Concern | Primary code | Related surface |
| --- | --- | --- |
| Development launcher and watch supervision | `launcher/Program.cs` | `scripts/publish-development-launcher.ps1` |
| Native startup and benchmark dispatch | `native/App/Program.cs` | `ApplicationSettings.cs`, `AppLog.cs` |
| Main form lifetime and reload dispatch | `native/App/AppHost.cs` | `HotReloadCoordinator.cs` |
| Metadata hot reload routing | `HotReloadHandler.cs`, `HotReloadModules.cs` | UI `Reload*ForHotReload` entry points |
| Editor restart state | `native/App/EditorRestartStore.cs` | `native/Engine/ProjectRestartSnapshot.cs` |
| Cross-process restart/shutdown readiness | `native/App/LauncherShutdownSignal.cs` | `launcher/Program.cs` |
| Native single-instance lease | `native/App/SingleInstanceLease.cs` | restart wait logic in `Program.cs` |
| Lifecycle regressions | `native/App/Benchmark.AppIntegration.cs` | `Benchmark.ProjectComposition.cs` |

```powershell
rg -n "HotReload|EditorRestart|SingleInstance|LauncherShutdown|startup|ready" launcher native/App native/UI
```
