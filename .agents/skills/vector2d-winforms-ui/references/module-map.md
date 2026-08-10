# WinForms UI Module Map

| Concern | Start here | Feature skill when needed |
| --- | --- | --- |
| Theme, shared controls, icons, motion | `Theme.cs`, `Modern*.cs`, `SvgIcons.cs`, `UiMotion.cs` | none |
| Workbench shell/docking | `DashDock.cs`, `WorkspaceTabs.cs`, `MainForm.InspectorAndWorkspace.cs` | owning workspace skill |
| General workbench commands/dialogs | `MainForm.WorkbenchCommands.cs`, `ModernDialogForm.cs` | assets/app/release as applicable |
| Material and color editing | `MaterialEditorPanel.cs`, `ProfessionalColorPickerDialog.cs`, `WorkspaceColorFlyout.cs` | drawing or brush |
| Layer/timeline presentation | `TimelineStrip.cs`, `TimelineStrip.Rendering.cs`, layer panels | `$vector2d-timeline-animation` or project model |
| Vault panels/tags/previews | `LibraryVaultPanel.cs`, `LibraryVaultPanel.Tags.cs` | `$vector2d-assets-persistence` |
| Scene/spatial controls | `SceneEditorPanel.cs`, `ReferenceViewPad.cs`, `SpatialTransformPanel.cs` | `$vector2d-scene-spatial` |
| Release notes/editor | `ReleaseNotesPanel.cs`, `release-manager/ReleaseManagerForm.cs` | `$vector2d-release-workflow` |
| Hot-reload routing | `native/App/HotReloadModules.cs`, root `Reload*ForHotReload` methods | `$vector2d-app-lifecycle` |

Search the type and bind/event callers before opening a large partial:

```powershell
rg -n "class <TypeName>|Bind[A-Z]|Interaction(Started|Completed|Canceled)|Reload.*HotReload" native/UI native/App release-manager
```
