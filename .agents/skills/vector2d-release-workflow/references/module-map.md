# Release Workflow Module Map

| Concern | Primary code | Related surface |
| --- | --- | --- |
| Source and assembly versions | `Directory.Build.props` | project files, generated file metadata |
| Authoritative in-app notes | `release/release-notes.json` | `native/App/ReleaseNotesCatalog.cs` |
| Release Manager data and rollback | `release-manager/ReleaseDocumentStore.cs` | `RepositoryLocator.cs` |
| Release Manager UI and preview | `ReleaseManagerForm.cs`, `ReleaseManagerTabControl.cs` | `native/UI/ReleaseNotesPanel.cs` |
| Publish process contract | `release-manager/ReleasePublishRunner.cs` | `scripts/run-release-manager.ps1`, `publish-release-manager.ps1` |
| Formal package assembly | `scripts/publish-single-exe.ps1` | `native/VectorAnimationEngine.Native.csproj` |
| Distribution bootstrap | `distribution-launcher/` | embedded application/runtime installer classes |
| Release documentation | `docs/RELEASE_MANAGER.md`, `docs/RELEASE_NOTES_*.md` | `README.md`, `docs/README.md` |

```powershell
rg -n "Version|AssemblyVersion|FileVersion|InformationalVersion" Directory.Build.props release-manager scripts distribution-launcher
rg -n "release-notes|Expected.*Sha256|validate-single-exe|deploy-embedded" native release-manager scripts distribution-launcher
```
