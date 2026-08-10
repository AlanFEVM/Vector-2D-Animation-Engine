# Timeline And Animation Module Map

| Concern | Primary code | Related surface |
| --- | --- | --- |
| Tracks, keys, exposures, snapshots, batch notifications | `native/Engine/AnimationTimeline.cs` | `CompositionDefinition.cs` |
| Tween definitions, curves, validation | `native/Engine/TimelineTween.cs` | `native/Engine/VectorScene.Tweens.cs` |
| Layer/Cel ownership and frame content | `native/Engine/VectorScene.LayersAndTimeline.cs` | `VectorScene.Snapshots.cs` |
| Timeline commands, undo, playback orchestration | `native/UI/MainForm.TimelineAndHistory.cs` | `MainForm.cs` |
| Timeline interaction and layout | `native/UI/TimelineStrip.cs` | `TimelineStrip.Rendering.cs` |
| Easing curve editor | `native/UI/TweenCurveEditorPanel.cs` | `PlaybackSettingsPanel.cs` |
| Timeline regressions | `native/App/Benchmark.TimelineSuite.cs` | `Benchmark.ProjectComposition.cs` |
| Tween regressions | `native/App/Benchmark.TimelineTween.cs` | `Benchmark.BezierGeometry.cs` |

```powershell
rg -n "EvaluateExposure|Insert(Frame|Keyframe)|RemoveFrame|ClearKeyframe|SynchronizeTracks" native/Engine native/UI
rg -n "TimelineTween|AutoKey|OnionSkin|Playback" native/Engine native/UI native/App
```
