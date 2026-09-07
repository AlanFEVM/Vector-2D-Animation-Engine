---
type: "query"
date: "2026-08-11T08:48:33.000047+00:00"
question: "How does MainForm route 3D keyboard input to StageControl reference camera movement, and which shortcuts conflict with arrow keys or WASD?"
contributor: "graphify"
outcome: "useful"
source_nodes: [".ProcessCmdKey()", ".HandleSpatialTransformShortcut()", "ShortcutProfiles", "StageControl", "InputFocusDismissalFilter"]
---

# Q: How does MainForm route 3D keyboard input to StageControl reference camera movement, and which shortcuts conflict with arrow keys or WASD?

## Answer

Expanded from original query via graph vocabulary: [camera, reference, pan, keyboard, shortcut, spatial, command, process, focus, input, keys, control]. The graph connected MainForm.ProcessCmdKey with focus-sensitive shortcut routing, HandleSpatialTransformShortcut, ShortcutProfiles, and StageControl camera behavior; it also exposed editor and interactive-control owners that must retain arrow navigation.

## Outcome

- Signal: useful

## Source Nodes

- .ProcessCmdKey()
- .HandleSpatialTransformShortcut()
- ShortcutProfiles
- StageControl
- InputFocusDismissalFilter