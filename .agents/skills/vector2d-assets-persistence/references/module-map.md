# Assets And Persistence Module Map

| Concern | Primary code | Related surface |
| --- | --- | --- |
| Managed project save/open and journal recovery | `native/App/ProjectVaultStore.cs` | `native/Engine/ProjectRestartSnapshot.cs` |
| Project roots and stable asset ownership | `native/Engine/VectorProject.cs` | `DrawingObjectDefinition.cs`, `SceneDefinition.cs` |
| Asset folders and tags | `ProjectAssetFolder.cs`, `ProjectAssetTag.cs`, `VectorProject.AssetTags.cs` | `native/UI/LibraryVaultPanel.Tags.cs`, `MainForm.AssetTags.cs` |
| SVG whole-object codec | `native/Engine/DrawingObjectSvgCodec.cs` | `native/Rendering/ImportedSvgRasterizer.cs` |
| SVG Break Apart | `native/Engine/ImportedSvgBreakApart.cs` | `native/UI/MainForm.WorkbenchCommands.cs` |
| Vault browser and previews | `native/UI/LibraryVaultPanel.cs` | `LibraryVaultPanel.Tags.cs` |
| Persistence regressions | `native/App/Benchmark.ProjectComposition.cs` | `Benchmark.AssetTags.cs` |

Search by contract before opening the large store:

```powershell
rg -n "Save\(|Load\(|RecoverInterruptedSave|ValidateManifest|CommitManagedFiles" native/App/ProjectVaultStore.cs
rg -n "Asset(Tag|Folder)|DrawingObjectSvgCodec|ImportedSvgBreakApart" native/Engine native/UI native/App
```
