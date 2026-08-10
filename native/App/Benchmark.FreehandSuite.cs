using System.Diagnostics;

namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    public static void RunFreehandStress()
    {
        RunLinkedFillBoundaryRegression();
        RunShapeToolRegression();
        RunPencilSmoothingRegression();
        RunPencilBezierRegression();
        RunRenderOrderRegression();
        RunDrawingTopologyRegression();
        RunBezierOperationPerformanceRegression();
        RunTraditionalBrushContourRegression();
        RunBrushGradientRegression();
        RunMixingBrushRegression();
        RunComplexBrushCommitPerformanceRegression();

        const int strokeCount = 512;
        const int samplesPerStroke = 256;
        var scene = new VectorScene();
        scene.CreateEmpty();
        var raw = new PointF[samplesPerStroke];
        var retainedPoints = 0;
        var build = Stopwatch.StartNew();

        for (var stroke = 0; stroke < strokeCount; stroke++)
        {
            var baseY = -10_000 + stroke * 38f;
            for (var sample = 0; sample < samplesPerStroke; sample++)
            {
                var x = -18_000 + sample * 140f;
                var y = baseY + MathF.Sin(sample * 0.18f + stroke * 0.07f) * 90f;
                raw[sample] = new PointF(x, y);
            }

            var points = FreehandStrokeProcessor.Process(raw, 58, 12);
            retainedPoints += points.Length;
            scene.AddFreehandStroke(
                0,
                points,
                VectorUnits.StrokePointsToUnits(stroke % 2 == 0 ? 2 : 8),
                stroke % 2 == 0 ? Color.White : Color.FromArgb(79, 179, 162),
                brushStroke: stroke % 2 != 0,
                (uint)Math.Max(3, points.Length));
        }

        build.Stop();
        if (scene.ObjectCount != strokeCount) throw new InvalidOperationException("Freehand benchmark did not retain every stroke.");
        const int pencilIndex = strokeCount - 2;
        if (scene.ShapeKind[pencilIndex] != ShapeKind.Freeform
            || !scene.TryGetFreehandWorldPoints(pencilIndex, out var pencilPoints)
            || pencilPoints.Length < 2)
        {
            throw new InvalidOperationException("Pencil geometry was not retained as an open freehand path.");
        }

        var hit = scene.HitTest(pencilPoints[pencilPoints.Length / 2], 0, VectorUnits.StrokePointsToUnits(2));
        if (hit < 0) throw new InvalidOperationException("Freehand hit testing failed.");

        var snapshot = scene.CreateSnapshot();
        scene.RemoveObjectAt(100);
        if (scene.ObjectCount != strokeCount - 1
            || scene.ShapeKind[100] != ShapeKind.Path
            || scene.TryGetFreehandWorldPoints(100, out _)
            || !scene.TryGetFreehandWorldPoints(pencilIndex, out _))
        {
            throw new InvalidOperationException("Freehand compact removal failed.");
        }

        scene.RestoreSnapshot(snapshot);
        if (scene.ObjectCount != strokeCount || !scene.TryGetFreehandWorldPoints(pencilIndex, out _)) throw new InvalidOperationException("Freehand snapshot restore failed.");

        var mergeScene = new VectorScene();
        mergeScene.CreateEmpty();
        var brushColor = Color.FromArgb(79, 179, 162);
        var brushWidth = VectorUnits.StrokePointsToUnits(8);
        var firstBrush = mergeScene.AddFreehandStroke(
            0,
            new[] { new PointF(-160, 0), new PointF(160, 0) },
            brushWidth,
            brushColor,
            brushStroke: true,
            32);
        var secondBrush = mergeScene.AddFreehandStroke(
            0,
            new[] { new PointF(0, -160), new PointF(0, 160) },
            brushWidth,
            brushColor,
            brushStroke: true,
            32);
        if (firstBrush < 0 || secondBrush < 0 || mergeScene.ObjectCount != 2) throw new InvalidOperationException("Brush fill setup failed.");

        var mergedBrush = mergeScene.MergeSameColorFillsAround(secondBrush);
        if (mergeScene.ObjectCount != 1 || (uint)mergedBrush >= mergeScene.ObjectCount)
        {
            throw new InvalidOperationException($"Intersecting same-color brush fills did not merge: objects={mergeScene.ObjectCount}, result={mergedBrush}.");
        }

        if (mergeScene.ShapeKind[mergedBrush] != ShapeKind.Path
            || mergeScene.Argb[mergedBrush] != brushColor.ToArgb()
            || mergeScene.Stroke[mergedBrush] != 0)
        {
            throw new InvalidOperationException(
                $"Merged brush fill was not an unstroked path: shape={mergeScene.ShapeKind[mergedBrush]}, fill={mergeScene.Argb[mergedBrush]}, stroke={mergeScene.Stroke[mergedBrush]}.");
        }

        var nearbyScene = new VectorScene();
        nearbyScene.CreateEmpty();
        nearbyScene.AddFreehandStroke(
            0,
            new[] { new PointF(-160, 0), new PointF(160, 0) },
            brushWidth,
            brushColor,
            brushStroke: true,
            32);
        var nearbyBrush = nearbyScene.AddFreehandStroke(
            0,
            new[] { new PointF(265, 0), new PointF(585, 0) },
            brushWidth,
            brushColor,
            brushStroke: true,
            32);
        nearbyScene.MergeSameColorFillsAround(nearbyBrush);
        if (nearbyScene.ObjectCount != 1)
        {
            throw new InvalidOperationException($"Same-color brush fills separated by less than 10 vu did not merge: objects={nearbyScene.ObjectCount}.");
        }

        var foldbackScene = new VectorScene();
        foldbackScene.CreateEmpty();
        var foldbackCenterline = new[]
        {
            new PointF(0, 0),
            new PointF(240, 0),
            new PointF(20, 10),
            new PointF(240, 20),
            new PointF(0, 30)
        };
        var firstFoldback = foldbackScene.AddFreehandStroke(0, foldbackCenterline, brushWidth, brushColor, brushStroke: true, 32);
        AssertBrushCoversCenterline(foldbackScene, firstFoldback, foldbackCenterline);

        var overpaintCenterline = foldbackCenterline
            .Select(point => new PointF(point.X, point.Y + 18))
            .ToArray();
        var overpaintBrush = foldbackScene.AddFreehandStroke(0, overpaintCenterline, brushWidth, brushColor, brushStroke: true, 32);
        var mergedOverpaint = foldbackScene.MergeSameColorFillsAround(overpaintBrush);
        if (foldbackScene.ObjectCount != 1 || (uint)mergedOverpaint >= foldbackScene.ObjectCount)
        {
            throw new InvalidOperationException($"Repeated foldback brush painting did not merge: objects={foldbackScene.ObjectCount}, result={mergedOverpaint}.");
        }

        AssertBrushCoversCenterline(foldbackScene, mergedOverpaint, foldbackCenterline);
        AssertBrushCoversCenterline(foldbackScene, mergedOverpaint, overpaintCenterline);

        var crossingScene = new VectorScene();
        crossingScene.CreateEmpty();
        var crossingCenterline = new[]
        {
            new PointF(-160, -160),
            new PointF(160, 160),
            new PointF(-160, 160),
            new PointF(160, -160)
        };
        var crossingBrush = -1;
        for (var pass = 0; pass < 3; pass++)
        {
            crossingBrush = crossingScene.AddFreehandStroke(0, crossingCenterline, brushWidth, brushColor, brushStroke: true, 32);
            crossingBrush = crossingScene.MergeSameColorFillsAround(crossingBrush);
            if (crossingScene.ObjectCount != 1 || (uint)crossingBrush >= crossingScene.ObjectCount)
            {
                throw new InvalidOperationException($"Repeated crossing brush painting did not remain one fill: pass={pass}, objects={crossingScene.ObjectCount}.");
            }

            AssertBrushCoversCenterline(crossingScene, crossingBrush, crossingCenterline);
        }

        Console.WriteLine($"strokes={strokeCount}");
        Console.WriteLine($"raw_points={strokeCount * samplesPerStroke}");
        Console.WriteLine($"retained_points={retainedPoints}");
        Console.WriteLine($"brush_merge_objects={mergeScene.ObjectCount}");
        Console.WriteLine($"nearby_brush_merge_objects={nearbyScene.ObjectCount}");
        Console.WriteLine($"foldback_overpaint_objects={foldbackScene.ObjectCount}");
        Console.WriteLine($"crossing_overpaint_objects={crossingScene.ObjectCount}");
        Console.WriteLine($"build_ms={build.Elapsed.TotalMilliseconds:0.0}");
        Console.WriteLine($"avg_commit_ms={build.Elapsed.TotalMilliseconds / strokeCount:0.000}");
    }

    private static void RunRenderOrderRegression()
    {
        var outlineScene = new VectorScene();
        outlineScene.CreateEmpty();
        outlineScene.AddObject(0, PointF.Empty, new SizeF(80, 60), 0, 0, Color.Coral, 4, ShapeKind.Rectangle);
        outlineScene.SetLayerOutline(0, true);
        if (!outlineScene.HasLayerOutline
            || outlineScene.HasLayerEffects
            || !outlineScene.HasDisplayLayerEffects
            || !SceneRenderOrder.RequiresObjectRenderer(outlineScene))
        {
            throw new InvalidOperationException("Outline-only layers did not force object rendering without becoming geometry layer effects.");
        }

        var scene = new VectorScene();
        scene.CreateEmpty(2);
        var strokeWidth = VectorUnits.StrokePointsToUnits(2);
        var topLine = scene.AddLineSegment(0, new PointF(-100, 0), new PointF(100, 0), strokeWidth, Color.Transparent, Color.White, 6);
        var bottomLine = scene.AddLineSegment(1, new PointF(-100, 0), new PointF(100, 0), strokeWidth, Color.Transparent, Color.White, 6);
        var topFill = scene.AddObject(0, PointF.Empty, new SizeF(200, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var bottomFill = scene.AddObject(1, PointF.Empty, new SizeF(200, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var topOutlinedFill = scene.AddObject(0, PointF.Empty, new SizeF(160, 90), 0, strokeWidth, Color.Teal, Color.White, 12, ShapeKind.Rectangle);
        var topText = scene.AddTextObject(
            0,
            PointF.Empty,
            new TextObjectData(
                "T",
                TextGeometry.FallbackFontFamilyName,
                12,
                TextFontStyle.Regular,
                TextHorizontalAlignment.Center,
                new SizeF(300, 600)),
            Color.Gold);

        var renderOrder = new SceneRenderOrderBuffer();
        renderOrder.Collect(scene, new RectangleF(-500, -500, 1000, 1000), 0);
        var commands = new List<string>();
        var drawn = renderOrder.Draw(
            scene,
            int.MaxValue,
            index => commands.Add($"F{index}"),
            index => commands.Add($"S{index}"));
        var expected = new[]
        {
            $"F{bottomFill}", $"S{bottomLine}",
            $"F{topFill}", $"F{topOutlinedFill}", $"F{topText}", $"S{topLine}", $"S{topOutlinedFill}"
        };
        if (drawn != scene.ObjectCount
            || !commands.SequenceEqual(expected)
            || !SceneRenderOrder.RequiresObjectRenderer(scene)
            || !SceneRenderOrder.HasFill(ShapeKind.Text)
            || SceneRenderOrder.HasStroke(ShapeKind.Text, strokeWidth))
        {
            throw new InvalidOperationException($"Render priority order is invalid: {string.Join(',', commands)}.");
        }

        var coveredLineScene = new VectorScene();
        coveredLineScene.CreateEmpty(2);
        var coveredLine = coveredLineScene.AddLineSegment(1, new PointF(-100, 0), new PointF(100, 0), strokeWidth, Color.Transparent, Color.White, 6);
        var coveringFill = coveredLineScene.AddObject(0, PointF.Empty, new SizeF(200, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var coveredHit = coveredLineScene.HitTestElement(PointF.Empty, 0, 0);
        if (!coveredHit.IsValid || coveredHit.Key.ObjectIndex != coveringFill || coveredHit.Key.Kind != DrawingElementKind.Fill)
        {
            throw new InvalidOperationException($"Higher-layer fill did not outrank lower-layer stroke: line={coveredLine}, hit={coveredHit.Key}.");
        }

        var sameLayerScene = new VectorScene();
        sameLayerScene.CreateEmpty();
        sameLayerScene.AddObject(0, PointF.Empty, new SizeF(200, 120), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var foregroundLine = sameLayerScene.AddLineSegment(0, new PointF(-100, 0), new PointF(100, 0), strokeWidth, Color.Transparent, Color.White, 6);
        var sameLayerHit = sameLayerScene.HitTestElement(PointF.Empty, 0, 0);
        if (!sameLayerHit.IsValid || sameLayerHit.Key.ObjectIndex != foregroundLine || sameLayerHit.Key.Kind != DrawingElementKind.Stroke)
        {
            throw new InvalidOperationException($"Same-layer stroke did not outrank fill: hit={sameLayerHit.Key}.");
        }

        var stackScene = new VectorScene();
        stackScene.CreateEmpty(2);
        var stackBack = stackScene.AddObject(0, PointF.Empty, new SizeF(160, 100), 0, 0, Color.Teal, Color.Transparent, 8, ShapeKind.Rectangle);
        var stackMiddle = stackScene.AddObject(0, PointF.Empty, new SizeF(140, 90), 0, 0, Color.Coral, Color.Transparent, 8, ShapeKind.Rectangle);
        var stackFront = stackScene.AddObject(0, PointF.Empty, new SizeF(120, 80), 0, 0, Color.Gold, Color.Transparent, 8, ShapeKind.Rectangle);
        var otherLayer = stackScene.AddObject(1, PointF.Empty, new SizeF(100, 70), 0, 0, Color.White, Color.Transparent, 8, ShapeKind.Rectangle);
        var otherLayerOrder = stackScene.ObjectOrder[otherLayer];
        if (stackScene.HitTestElement(PointF.Empty, 0, 0).Key.ObjectIndex != stackFront
            || stackScene.CanMoveObjectsInLayerStack([stackFront], 1, 0)
            || !stackScene.MoveObjectsInLayerStack([stackFront], -1, 0)
            || stackScene.HitTestElement(PointF.Empty, 0, 0).Key.ObjectIndex != stackMiddle
            || !stackScene.MoveObjectsInLayerStack([stackBack, stackFront], 1, 0)
            || stackScene.HitTestElement(PointF.Empty, 0, 0).Key.ObjectIndex != stackFront
            || stackScene.ObjectOrder[otherLayer] != otherLayerOrder)
        {
            throw new InvalidOperationException("Single-layer object stacking did not move the selection by one level without crossing layers.");
        }

        RunDrawingObjectStackShortcutRegression();
        RunSceneInstanceStackShortcutRegression();
        RunConvertSelectionToSymbolShortcutRegression();
        RunMixedClipboardAndSceneDeleteShortcutRegression();

        var edgeScene = new VectorScene();
        edgeScene.CreateEmpty();
        var edgeLine = edgeScene.AddLineSegment(0, new PointF(2, -200), new PointF(2, 20), strokeWidth, Color.Transparent, Color.White, 6);
        var edgeCurve = edgeScene.AddLineSegment(0, new PointF(-20, -100), new PointF(20, -100), strokeWidth, Color.Transparent, Color.White, 6);
        edgeScene.CurveControlX[edgeCurve] = 0;
        edgeScene.CurveControlY[edgeCurve] = 140;
        edgeScene.CurveControl2X[edgeCurve] = 0;
        edgeScene.CurveControl2Y[edgeCurve] = 140;
        var edgeRotated = edgeScene.AddObject(0, new PointF(12, 5), new SizeF(4, 20), MathF.PI / 4, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        var edgeOutlined = edgeScene.AddObject(0, new PointF(12, 5), new SizeF(4, 4), 0, 16, Color.Teal, Color.White, 12, ShapeKind.Rectangle);
        edgeScene.RebuildGeometryIndex();

        renderOrder.Collect(edgeScene, new RectangleF(0, 0, 5, 10), 0);
        var edgeCommands = new List<string>();
        var edgeDrawn = renderOrder.Draw(
            edgeScene,
            int.MaxValue,
            index => edgeCommands.Add($"F{index}"),
            index => edgeCommands.Add($"S{index}"));
        var expectedEdgeCommands = new[] { $"F{edgeRotated}", $"F{edgeOutlined}", $"S{edgeLine}", $"S{edgeCurve}", $"S{edgeOutlined}" };
        if (renderOrder.VisibleCount != edgeScene.ObjectCount
            || edgeDrawn != edgeScene.ObjectCount
            || !edgeCommands.SequenceEqual(expectedEdgeCommands))
        {
            throw new InvalidOperationException($"World-bounds render culling is invalid: {string.Join(',', edgeCommands)}.");
        }

        Console.WriteLine($"render_order_commands={commands.Count}");
    }

    private static void RunDrawingObjectStackShortcutRegression()
    {
        var moveForward = Keys.Control | Keys.Up;
        var moveBackward = Keys.Control | Keys.Down;
        if (MainForm.ResolveDrawingObjectStackShortcutDirection(
                moveForward,
                canvasShortcutsEnabled: true,
                hierarchyTreeFocused: false,
                focusedEditor: false,
                materialEditActive: false) != 1
            || MainForm.ResolveDrawingObjectStackShortcutDirection(
                moveBackward,
                canvasShortcutsEnabled: true,
                hierarchyTreeFocused: false,
                focusedEditor: false,
                materialEditActive: false) != -1
            || MainForm.ResolveDrawingObjectStackShortcutDirection(
                moveForward,
                canvasShortcutsEnabled: false,
                hierarchyTreeFocused: true,
                focusedEditor: false,
                materialEditActive: false) != 1
            || MainForm.ResolveDrawingObjectStackShortcutDirection(
                moveBackward,
                canvasShortcutsEnabled: false,
                hierarchyTreeFocused: true,
                focusedEditor: false,
                materialEditActive: false) != -1
            || MainForm.ResolveDrawingObjectStackShortcutDirection(
                moveForward,
                canvasShortcutsEnabled: false,
                hierarchyTreeFocused: false,
                focusedEditor: false,
                materialEditActive: false) != 0
            || MainForm.ResolveDrawingObjectStackShortcutDirection(
                moveBackward,
                canvasShortcutsEnabled: false,
                hierarchyTreeFocused: false,
                focusedEditor: false,
                materialEditActive: false) != 0
            || MainForm.ResolveDrawingObjectStackShortcutDirection(
                moveForward,
                canvasShortcutsEnabled: true,
                hierarchyTreeFocused: true,
                focusedEditor: true,
                materialEditActive: false) != 0
            || MainForm.ResolveDrawingObjectStackShortcutDirection(
                moveForward,
                canvasShortcutsEnabled: true,
                hierarchyTreeFocused: true,
                focusedEditor: false,
                materialEditActive: true) != 0
            || MainForm.ResolveDrawingObjectStackShortcutDirection(
                Keys.Control | Keys.Left,
                canvasShortcutsEnabled: true,
                hierarchyTreeFocused: false,
                focusedEditor: false,
                materialEditActive: false) != 0
            || !MainForm.BlocksModelCommandDuringPointerInteraction(moveForward)
            || !MainForm.BlocksModelCommandDuringPointerInteraction(moveBackward))
        {
            throw new InvalidOperationException(
                "Drawing-object stack shortcut focus routing did not preserve canvas/timeline access while protecting editors, interactive controls, material edits, and pointer interactions.");
        }

        const System.Reflection.BindingFlags instanceFlags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var sceneField = typeof(MainForm).GetField("_scene", instanceFlags)
            ?? throw new InvalidOperationException("Object-stack shortcut regression could not find the drawing scene.");
        var hierarchyField = typeof(MainForm).GetField("_hierarchyPanel", instanceFlags)
            ?? throw new InvalidOperationException("Object-stack shortcut regression could not find the Hierarchy panel.");
        var processCmdKey = typeof(MainForm).GetMethod(
                "ProcessCmdKey",
                instanceFlags,
                binder: null,
                types: [typeof(Message).MakeByRefType(), typeof(Keys)],
                modifiers: null)
            ?? throw new InvalidOperationException("Object-stack shortcut regression could not find ProcessCmdKey.");
        var undoLastEdit = typeof(MainForm).GetMethod(
                "UndoLastEdit",
                instanceFlags,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null)
            ?? throw new InvalidOperationException("Object-stack shortcut regression could not find UndoLastEdit.");

        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000)
        };
        var scene = sceneField.GetValue(form) as VectorScene
            ?? throw new InvalidOperationException("Object-stack shortcut regression did not obtain the drawing scene.");
        var hierarchy = hierarchyField.GetValue(form) as HierarchyPanel
            ?? throw new InvalidOperationException("Object-stack shortcut regression did not obtain the Hierarchy panel.");

        scene.CreateEmpty(2);
        var back = scene.AddObject(
            0,
            new PointF(-180, 0),
            new SizeF(120, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var middle = scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(120, 80),
            0,
            0,
            Color.Coral,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var front = scene.AddObject(
            0,
            new PointF(180, 0),
            new SizeF(120, 80),
            0,
            0,
            Color.Gold,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var otherLayer = scene.AddObject(
            1,
            new PointF(0, 180),
            new SizeF(120, 80),
            0,
            0,
            Color.White,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        if (!scene.InsertTimelineKeyframe(0, 10))
        {
            throw new InvalidOperationException("Object-stack shortcut regression could not create an isolated Cel.");
        }
        scene.EditFrame = 0;

        var otherCelObjects = Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.ObjectLayer[index] == 0 && scene.ObjectKeyframeFrame[index] == 10)
            .ToArray();
        if (otherCelObjects.Length != 3)
        {
            throw new InvalidOperationException(
                $"Object-stack shortcut regression created an unexpected isolated Cel: objects={otherCelObjects.Length}.");
        }

        var isolatedObjects = otherCelObjects.Append(otherLayer).Distinct().ToArray();
        var isolatedOrder = isolatedObjects
            .Select(index => (Index: index, Order: scene.ObjectOrder[index], SubOrder: scene.ObjectSubOrder[index]))
            .ToArray();
        int[] CurrentCelOrder() => Enumerable.Range(0, scene.ObjectCount)
            .Where(index => scene.ObjectLayer[index] == 0 && scene.ObjectKeyframeFrame[index] == 0)
            .OrderBy(index => scene.ObjectOrder[index])
            .ThenBy(index => scene.ObjectSubOrder[index])
            .ThenBy(index => index)
            .ToArray();
        bool IsolatedOrderUnchanged() => isolatedOrder.All(item =>
            scene.ObjectOrder[item.Index] == item.Order
            && scene.ObjectSubOrder[item.Index] == item.SubOrder);

        hierarchy.BindScene(scene);
        form.Show();
        for (Control? control = hierarchy;
             control is not null && !ReferenceEquals(control, form);
             control = control.Parent)
        {
            control.Visible = true;
            control.BringToFront();
        }
        form.Activate();
        Application.DoEvents();
        var hierarchyTree = hierarchy.Controls.OfType<TreeView>().SingleOrDefault()
            ?? throw new InvalidOperationException("Object-stack shortcut regression did not find the Hierarchy TreeView.");

        void FocusHierarchyObject(int objectIndex)
        {
            hierarchyTree.SelectedNode = null;
            hierarchy.SelectObject(objectIndex);
            hierarchyTree.Select();
            hierarchyTree.Focus();
            Application.DoEvents();
            if (!hierarchyTree.Focused || !hierarchy.ContainsFocus)
            {
                throw new InvalidOperationException("Object-stack shortcut regression could not focus the Hierarchy TreeView.");
            }
        }

        bool InvokeShortcut(Keys keyData)
        {
            object[] arguments =
            [
                Message.Create(form.Handle, 0, IntPtr.Zero, IntPtr.Zero),
                keyData
            ];
            return processCmdKey.Invoke(form, arguments) is true;
        }

        bool Undo() => undoLastEdit.Invoke(form, null) is true;
        var initialOrder = new[] { back, middle, front };
        if (!CurrentCelOrder().SequenceEqual(initialOrder) || !IsolatedOrderUnchanged())
        {
            throw new InvalidOperationException("Object-stack shortcut regression did not start from the expected layer/Cel order.");
        }

        FocusHierarchyObject(middle);
        if (!InvokeShortcut(moveForward)
            || !CurrentCelOrder().SequenceEqual([back, front, middle])
            || !IsolatedOrderUnchanged())
        {
            throw new InvalidOperationException(
                "Ctrl+Up from the focused Hierarchy TreeView did not move only the selected current-Cel object forward.");
        }
        if (!Undo()
            || !CurrentCelOrder().SequenceEqual(initialOrder)
            || !IsolatedOrderUnchanged())
        {
            throw new InvalidOperationException("Undo did not restore the Ctrl+Up object-stack change.");
        }

        FocusHierarchyObject(middle);
        if (!InvokeShortcut(moveBackward)
            || !CurrentCelOrder().SequenceEqual([middle, back, front])
            || !IsolatedOrderUnchanged())
        {
            throw new InvalidOperationException(
                "Ctrl+Down from the focused Hierarchy TreeView did not move only the selected current-Cel object backward.");
        }
        if (!Undo()
            || !CurrentCelOrder().SequenceEqual(initialOrder)
            || !IsolatedOrderUnchanged())
        {
            throw new InvalidOperationException("Undo did not restore the Ctrl+Down object-stack change.");
        }

        FocusHierarchyObject(front);
        if (InvokeShortcut(moveForward)
            || !CurrentCelOrder().SequenceEqual(initialOrder)
            || !IsolatedOrderUnchanged())
        {
            throw new InvalidOperationException("Ctrl+Up consumed or mutated an already-frontmost Hierarchy selection.");
        }

        FocusHierarchyObject(back);
        if (InvokeShortcut(moveBackward)
            || !CurrentCelOrder().SequenceEqual(initialOrder)
            || !IsolatedOrderUnchanged())
        {
            throw new InvalidOperationException("Ctrl+Down consumed or mutated an already-backmost Hierarchy selection.");
        }

        Console.WriteLine("drawing_object_stack_shortcuts=ok");
    }

    private static void RunConvertSelectionToSymbolShortcutRegression()
    {
        const System.Reflection.BindingFlags instanceFlags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var projectField = typeof(MainForm).GetField("_project", instanceFlags)
            ?? throw new InvalidOperationException("Convert-to-symbol shortcut regression could not find the project.");
        var sceneField = typeof(MainForm).GetField("_scene", instanceFlags)
            ?? throw new InvalidOperationException("Convert-to-symbol shortcut regression could not find the drawing scene.");
        var setSelection = typeof(MainForm).GetMethod(
                "SetSelection",
                instanceFlags,
                binder: null,
                types: [typeof(IEnumerable<int>), typeof(bool)],
                modifiers: null)
            ?? throw new InvalidOperationException("Convert-to-symbol shortcut regression could not select drawing objects.");
        var processCmdKey = typeof(MainForm).GetMethod(
                "ProcessCmdKey",
                instanceFlags,
                binder: null,
                types: [typeof(Message).MakeByRefType(), typeof(Keys)],
                modifiers: null)
            ?? throw new InvalidOperationException("Convert-to-symbol shortcut regression could not find ProcessCmdKey.");
        var undoLastEdit = typeof(MainForm).GetMethod(
                "UndoLastEdit",
                instanceFlags,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null)
            ?? throw new InvalidOperationException("Convert-to-symbol shortcut regression could not find UndoLastEdit.");

        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000)
        };
        var project = projectField.GetValue(form) as VectorProject
            ?? throw new InvalidOperationException("Convert-to-symbol shortcut regression did not obtain the project.");
        var scene = sceneField.GetValue(form) as VectorScene
            ?? throw new InvalidOperationException("Convert-to-symbol shortcut regression did not obtain the drawing scene.");
        scene.CreateEmpty(1, 8);
        var first = scene.AddObject(
            0,
            new PointF(-80, 20),
            new SizeF(100, 60),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        var second = scene.AddObject(
            0,
            new PointF(100, 40),
            new SizeF(80, 80),
            0,
            0,
            Color.Coral,
            Color.Transparent,
            8,
            ShapeKind.Ellipse);
        setSelection.Invoke(form, [new[] { first, second }, false]);
        form.Show();
        form.Activate();
        Application.DoEvents();

        object[] arguments =
        [
            Message.Create(form.Handle, 0, IntPtr.Zero, IntPtr.Zero),
            Keys.F8
        ];
        if (processCmdKey.Invoke(form, arguments) is not true
            || project.DrawingObjects.Count != 2
            || project.DrawingObjects[0].Scene.ObjectCount != 0
            || project.DrawingObjects[0].Instances.Count != 1
            || project.DrawingObjects[1].Scene.ObjectCount != 2)
        {
            throw new InvalidOperationException(
                "F8 did not convert the selected drawing objects into a nested symbol in the current symbol.");
        }
        if (undoLastEdit.Invoke(form, null) is not true
            || project.DrawingObjects.Count != 1
            || project.DrawingObjects[0].Scene.ObjectCount != 2
            || project.DrawingObjects[0].Instances.Count != 0)
        {
            throw new InvalidOperationException(
                "Undo did not remove the converted symbol and restore its source drawing objects.");
        }

        Console.WriteLine("convert_selection_to_symbol_shortcut=ok");
    }

    private static void RunMixedClipboardAndSceneDeleteShortcutRegression()
    {
        const System.Reflection.BindingFlags instanceFlags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var projectField = typeof(MainForm).GetField("_project", instanceFlags)
            ?? throw new InvalidOperationException("Mixed clipboard regression could not find the project.");
        var workspaceTabsField = typeof(MainForm).GetField("_workspaceTabs", instanceFlags)
            ?? throw new InvalidOperationException("Mixed clipboard regression could not find the workspace tabs.");
        var stageField = typeof(MainForm).GetField("_stage", instanceFlags)
            ?? throw new InvalidOperationException("Mixed clipboard regression could not find the Stage.");
        var selectedObjectsField = typeof(MainForm).GetField("_selectedObjects", instanceFlags)
            ?? throw new InvalidOperationException("Mixed clipboard regression could not inspect drawing selection.");
        var selectedInstanceIdsField = typeof(MainForm).GetField("_selectedSceneInstanceIds", instanceFlags)
            ?? throw new InvalidOperationException("Mixed clipboard regression could not inspect instance selection.");
        var projectDirtyField = typeof(MainForm).GetField("_projectDirty", instanceFlags)
            ?? throw new InvalidOperationException("Mixed clipboard regression could not suppress save prompts.");
        var setSelection = typeof(MainForm).GetMethod(
                "SetSelection",
                instanceFlags,
                binder: null,
                types: [typeof(int), typeof(bool)],
                modifiers: null)
            ?? throw new InvalidOperationException("Mixed clipboard regression could not select a drawing object.");
        var setInstanceSelection = typeof(MainForm).GetMethod(
                "SetSceneInstanceSelection",
                instanceFlags,
                binder: null,
                types: [typeof(DrawingObjectInstanceDefinition), typeof(bool)],
                modifiers: null)
            ?? throw new InvalidOperationException("Mixed clipboard regression could not select an instance.");
        var processCmdKey = typeof(MainForm).GetMethod(
                "ProcessCmdKey",
                instanceFlags,
                binder: null,
                types: [typeof(Message).MakeByRefType(), typeof(Keys)],
                modifiers: null)
            ?? throw new InvalidOperationException("Mixed clipboard regression could not find ProcessCmdKey.");
        var undoLastEdit = typeof(MainForm).GetMethod(
                "UndoLastEdit",
                instanceFlags,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null)
            ?? throw new InvalidOperationException("Mixed clipboard regression could not find UndoLastEdit.");

        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            Size = new Size(1280, 800)
        };
        try
        {
            var project = projectField.GetValue(form) as VectorProject
                ?? throw new InvalidOperationException("Mixed clipboard regression did not obtain the project.");
            var workspaceTabs = workspaceTabsField.GetValue(form) as WorkspaceTabs
                ?? throw new InvalidOperationException("Mixed clipboard regression did not obtain workspace tabs.");
            var stage = stageField.GetValue(form) as StageControl
                ?? throw new InvalidOperationException("Mixed clipboard regression did not obtain the Stage.");
            var container = project.DrawingObjects[0];
            container.Scene.CreateEmpty();
            var localObject = container.Scene.AddObject(
                0,
                new PointF(100, 80),
                new SizeF(90, 60),
                0,
                0,
                Color.Teal,
                Color.Transparent,
                8,
                ShapeKind.Rectangle);
            var child = project.AddDrawingObject("Mixed clipboard child");
            child.Scene.CreateEmpty();
            child.Scene.AddObject(
                0,
                PointF.Empty,
                new SizeF(70, 50),
                0,
                0,
                Color.Gold,
                Color.Transparent,
                8,
                ShapeKind.Ellipse);
            if (!project.TryAddDrawingObjectInstance(
                    container.Id,
                    child.Id,
                    new PointF(240, 120),
                    container.Scene.LayerIds[0],
                    out var nested)
                || nested is null)
            {
                throw new InvalidOperationException("Mixed clipboard regression could not create a nested instance.");
            }

            bool InvokeShortcut(Keys keyData)
            {
                object[] arguments =
                [
                    Message.Create(form.Handle, 0, IntPtr.Zero, IntPtr.Zero),
                    keyData
                ];
                return processCmdKey.Invoke(form, arguments) is true;
            }
            bool Undo() => undoLastEdit.Invoke(form, null) is true;
            void SelectMixed(int objectIndex, DrawingObjectInstanceDefinition instance)
            {
                setSelection.Invoke(form, [objectIndex, false]);
                setInstanceSelection.Invoke(form, [instance, true]);
                Application.DoEvents();
            }
            bool HasMixedSelection(int drawingCount, int instanceCount)
            {
                return selectedObjectsField.GetValue(form) is List<int> selectedObjects
                    && selectedObjects.Count == drawingCount
                    && selectedInstanceIdsField.GetValue(form) is HashSet<string> selectedIds
                    && selectedIds.Count == instanceCount;
            }

            form.Show();
            workspaceTabs.SelectedView = WorkspaceView.BasicDrawing;
            form.Activate();
            stage.Select();
            stage.Focus();
            Application.DoEvents();

            SelectMixed(localObject, nested);
            if (!HasMixedSelection(1, 1)
                || !InvokeShortcut(Keys.Control | Keys.C)
                || !InvokeShortcut(Keys.Control | Keys.V)
                || container.Scene.ObjectCount != 2
                || container.Instances.Count != 2
                || !HasMixedSelection(1, 1))
            {
                throw new InvalidOperationException(
                    "Ctrl+C/Ctrl+V did not copy, paste, and select mixed drawing geometry plus nested instances.");
            }
            if (!Undo()
                || container.Scene.ObjectCount != 1
                || container.Instances.Count != 1)
            {
                throw new InvalidOperationException("One undo did not restore the mixed paste transaction.");
            }

            var restoredNested = container.Instances.Single();
            SelectMixed(0, restoredNested);
            if (!InvokeShortcut(Keys.Delete)
                || container.Scene.ObjectCount != 0
                || container.Instances.Count != 0)
            {
                throw new InvalidOperationException(
                    "Delete did not remove mixed drawing geometry plus nested instances in one command.");
            }
            if (!Undo()
                || container.Scene.ObjectCount != 1
                || container.Instances.Count != 1)
            {
                throw new InvalidOperationException("One undo did not restore the mixed delete transaction.");
            }

            var scene = project.Scenes[0];
            scene.Dimension = SceneDimension.TwoD;
            if (!project.TryAddSceneInstance(
                    scene.Id,
                    child.Id,
                    new PointF(360, 200),
                    0,
                    scene.Layers[0].Id,
                    out var sceneInstance)
                || sceneInstance is null)
            {
                throw new InvalidOperationException("Scene clipboard regression could not create a Scene instance.");
            }
            if (!project.TryAddSceneInstance(
                    scene.Id,
                    child.Id,
                    new PointF(520, 280),
                    0,
                    scene.Layers[0].Id,
                    out var secondSceneInstance)
                || secondSceneInstance is null)
            {
                throw new InvalidOperationException("Scene clipboard regression could not create its second Scene instance.");
            }

            workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
            Application.DoEvents();
            stage.Select();
            stage.Focus();
            setInstanceSelection.Invoke(form, [sceneInstance, false]);
            setInstanceSelection.Invoke(form, [secondSceneInstance, true]);
            Application.DoEvents();
            var sceneCopyHandled = InvokeShortcut(Keys.Control | Keys.C);
            var scenePasteHandled = InvokeShortcut(Keys.Control | Keys.V);
            var pastedScenePositions = scene.Instances
                .Skip(2)
                .Select(instance => instance.EvaluatePosition(0))
                .ToArray();
            if (!sceneCopyHandled
                || !scenePasteHandled
                || scene.Instances.Count != 4
                || !HasMixedSelection(0, 2)
                || !pastedScenePositions.Any(position =>
                    Math.Abs(position.X - (sceneInstance.X + 96)) <= 0.001f
                    && Math.Abs(position.Y - (sceneInstance.Y + 96)) <= 0.001f)
                || !pastedScenePositions.Any(position =>
                    Math.Abs(position.X - (secondSceneInstance.X + 96)) <= 0.001f
                    && Math.Abs(position.Y - (secondSceneInstance.Y + 96)) <= 0.001f))
            {
                throw new InvalidOperationException(
                    "Scene Building Ctrl+C/Ctrl+V did not duplicate and select both Scene instances with the paste offset.");
            }
            if (!Undo() || scene.Instances.Count != 2)
            {
                throw new InvalidOperationException("Scene Building undo did not restore the multi-instance paste.");
            }

            var restoredSceneInstances = scene.Instances.ToArray();
            setInstanceSelection.Invoke(form, [restoredSceneInstances[0], false]);
            setInstanceSelection.Invoke(form, [restoredSceneInstances[1], true]);
            Application.DoEvents();
            if (!InvokeShortcut(Keys.Delete) || scene.Instances.Count != 0)
            {
                throw new InvalidOperationException("Scene Building Delete did not remove both selected symbol instances.");
            }
            if (!Undo() || scene.Instances.Count != 2)
            {
                throw new InvalidOperationException("Scene Building undo did not restore both deleted symbol instances.");
            }

            Console.WriteLine("mixed_clipboard_scene_delete_shortcuts=ok");
        }
        finally
        {
            projectDirtyField.SetValue(form, false);
        }
    }

    private static void RunSceneInstanceStackShortcutRegression()
    {
        var moveForward = Keys.Control | Keys.Up;
        var moveBackward = Keys.Control | Keys.Down;
        const System.Reflection.BindingFlags instanceFlags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var projectField = typeof(MainForm).GetField("_project", instanceFlags)
            ?? throw new InvalidOperationException("Scene-instance stack shortcut regression could not find the project.");
        var workspaceTabsField = typeof(MainForm).GetField("_workspaceTabs", instanceFlags)
            ?? throw new InvalidOperationException("Scene-instance stack shortcut regression could not find the workspace tabs.");
        var stageField = typeof(MainForm).GetField("_stage", instanceFlags)
            ?? throw new InvalidOperationException("Scene-instance stack shortcut regression could not find the Stage.");
        var sceneField = typeof(MainForm).GetField("_scene", instanceFlags)
            ?? throw new InvalidOperationException("Scene-instance stack shortcut regression could not find the composed scene.");
        var compositionResultField = typeof(MainForm).GetField("_sceneCompositionResult", instanceFlags)
            ?? throw new InvalidOperationException("Scene-instance stack shortcut regression could not find the composition result.");
        var timelineField = typeof(MainForm).GetField("_timeline", instanceFlags)
            ?? throw new InvalidOperationException("Scene-instance stack shortcut regression could not find the timeline.");
        var selectedIdsField = typeof(MainForm).GetField("_selectedSceneInstanceIds", instanceFlags)
            ?? throw new InvalidOperationException("Scene-instance stack shortcut regression could not inspect selected instance IDs.");
        var primaryIdField = typeof(MainForm).GetField("_selectedSceneInstanceId", instanceFlags)
            ?? throw new InvalidOperationException("Scene-instance stack shortcut regression could not inspect the primary instance ID.");
        var projectDirtyField = typeof(MainForm).GetField("_projectDirty", instanceFlags)
            ?? throw new InvalidOperationException("Scene-instance stack shortcut regression could not suppress save prompts.");
        var setSceneInstanceSelection = typeof(MainForm).GetMethod(
                "SetSceneInstanceSelection",
                instanceFlags,
                binder: null,
                types:
                [
                    typeof(IEnumerable<DrawingObjectInstanceDefinition>),
                    typeof(DrawingObjectInstanceDefinition)
                ],
                modifiers: null)
            ?? throw new InvalidOperationException("Scene-instance stack shortcut regression could not find multi-instance selection.");
        var bindSceneEditStage = typeof(MainForm).GetMethod(
                "BindSceneEditStage",
                instanceFlags,
                binder: null,
                types: [typeof(bool)],
                modifiers: null)
            ?? throw new InvalidOperationException("Scene-instance stack shortcut regression could not bind Scene Building.");
        var processCmdKey = typeof(MainForm).GetMethod(
                "ProcessCmdKey",
                instanceFlags,
                binder: null,
                types: [typeof(Message).MakeByRefType(), typeof(Keys)],
                modifiers: null)
            ?? throw new InvalidOperationException("Scene-instance stack shortcut regression could not find ProcessCmdKey.");
        var undoLastEdit = typeof(MainForm).GetMethod(
                "UndoLastEdit",
                instanceFlags,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null)
            ?? throw new InvalidOperationException("Scene-instance stack shortcut regression could not find UndoLastEdit.");

        using var form = new MainForm
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-30_000, -30_000),
            Size = new Size(1280, 800)
        };
        try
        {
            var project = projectField.GetValue(form) as VectorProject
                ?? throw new InvalidOperationException("Scene-instance stack shortcut regression did not obtain the project.");
            var workspaceTabs = workspaceTabsField.GetValue(form) as WorkspaceTabs
                ?? throw new InvalidOperationException("Scene-instance stack shortcut regression did not obtain the workspace tabs.");
            var stage = stageField.GetValue(form) as StageControl
                ?? throw new InvalidOperationException("Scene-instance stack shortcut regression did not obtain the Stage.");
            var timeline = timelineField.GetValue(form) as TimelineStrip
                ?? throw new InvalidOperationException("Scene-instance stack shortcut regression did not obtain the timeline.");
            var sceneDefinition = project.Scenes[0];
            sceneDefinition.Dimension = SceneDimension.TwoD;
            var frontSource = project.DrawingObjects[0];
            frontSource.Name = "Shortcut front";
            var middleSource = project.AddDrawingObject("Shortcut middle");
            var backSource = project.AddDrawingObject("Shortcut back");

            static void ConfigurePureFill(DrawingObjectDefinition drawingObject, Color fill)
            {
                drawingObject.Scene.CreateEmpty();
                drawingObject.Scene.AddObject(
                    0,
                    PointF.Empty,
                    new SizeF(240, 160),
                    0,
                    0,
                    fill,
                    Color.Transparent,
                    8,
                    ShapeKind.Rectangle);
            }

            ConfigurePureFill(frontSource, Color.Teal);
            ConfigurePureFill(middleSource, Color.Coral);
            ConfigurePureFill(backSource, Color.Gold);
            var stackLayerId = sceneDefinition.Layers[0].Id;
            if (!project.TryAddSceneLayer(sceneDefinition.Id, out var isolatedLayer)
                || isolatedLayer is null
                || !project.TryAddSceneInstance(
                    sceneDefinition.Id,
                    frontSource.Id,
                    PointF.Empty,
                    0,
                    stackLayerId,
                    out var front)
                || front is null
                || !project.TryAddSceneInstance(
                    sceneDefinition.Id,
                    middleSource.Id,
                    PointF.Empty,
                    0,
                    stackLayerId,
                    out var middle)
                || middle is null
                || !project.TryAddSceneInstance(
                    sceneDefinition.Id,
                    backSource.Id,
                    PointF.Empty,
                    0,
                    stackLayerId,
                    out var back)
                || back is null
                || !project.TryAddSceneInstance(
                    sceneDefinition.Id,
                    frontSource.Id,
                    new PointF(600, 0),
                    0,
                    isolatedLayer.Id,
                    out var isolatedFirst)
                || isolatedFirst is null
                || !project.TryAddSceneInstance(
                    sceneDefinition.Id,
                    middleSource.Id,
                    new PointF(800, 0),
                    0,
                    isolatedLayer.Id,
                    out var isolatedSecond)
                || isolatedSecond is null)
            {
                throw new InvalidOperationException(
                    "Scene-instance stack shortcut regression could not create overlapping pure-fill instances and an isolated layer.");
            }

            var frontId = front.Id;
            var middleId = middle.Id;
            var backId = back.Id;
            var initialOrder = new[] { frontId, middleId, backId };
            var isolatedLayerId = isolatedLayer.Id;
            var isolatedOrder = new[] { isolatedFirst.Id, isolatedSecond.Id };
            string[] CurrentStackOrder() => sceneDefinition.InstancesInLayer(stackLayerId)
                .Select(instance => instance.Id)
                .ToArray();
            bool IsolatedLayerUnchanged() => sceneDefinition.InstancesInLayer(isolatedLayerId)
                .Select(instance => instance.Id)
                .SequenceEqual(isolatedOrder);
            DrawingObjectInstanceDefinition FindInstance(string instanceId) =>
                sceneDefinition.Instances.FirstOrDefault(instance =>
                    string.Equals(instance.Id, instanceId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Scene-instance stack shortcut regression lost instance {instanceId} after restore.");
            void SelectInstances(IReadOnlyList<string> instanceIds, string primaryId)
            {
                var instances = instanceIds.Select(FindInstance).ToArray();
                var primary = instances.First(instance =>
                    string.Equals(instance.Id, primaryId, StringComparison.Ordinal));
                setSceneInstanceSelection.Invoke(form, [instances, primary]);
                Application.DoEvents();
            }
            bool SelectionPreserved(IReadOnlyCollection<string> instanceIds, string primaryId)
            {
                return selectedIdsField.GetValue(form) is HashSet<string> selectedIds
                    && selectedIds.SetEquals(instanceIds)
                    && primaryIdField.GetValue(form) is string selectedPrimaryId
                    && string.Equals(selectedPrimaryId, primaryId, StringComparison.Ordinal);
            }
            bool InvokeShortcut(Keys keyData)
            {
                object[] arguments =
                [
                    Message.Create(form.Handle, 0, IntPtr.Zero, IntPtr.Zero),
                    keyData
                ];
                return processCmdKey.Invoke(form, arguments) is true;
            }
            bool Undo() => undoLastEdit.Invoke(form, null) is true;
            string FrontFillOwnerId()
            {
                var compositionScene = sceneField.GetValue(form) as VectorScene
                    ?? throw new InvalidOperationException(
                        "Scene-instance stack shortcut regression lost the composed scene.");
                var composition = compositionResultField.GetValue(form) as SceneCompositionResult
                    ?? throw new InvalidOperationException(
                        "Scene-instance stack shortcut regression lost the composition result.");
                var hit = compositionScene.HitTestElement(PointF.Empty, 0, 0);
                if (!hit.IsValid
                    || hit.Key.Kind != DrawingElementKind.Fill
                    || !composition.TryGetOwner(hit.Key.ObjectIndex, out var owner))
                {
                    return "";
                }

                return string.IsNullOrWhiteSpace(owner.RootInstanceId) ? owner.InstanceId : owner.RootInstanceId;
            }

            form.Show();
            workspaceTabs.SelectedView = WorkspaceView.SceneEditor;
            form.Activate();
            stage.Select();
            stage.Focus();
            Application.DoEvents();
            if (workspaceTabs.SelectedView != WorkspaceView.SceneEditor
                || !ReferenceEquals(timeline.Context, sceneDefinition)
                || stage.ReferenceDimension != SceneDimension.TwoD
                || !stage.ContainsFocus
                || !CurrentStackOrder().SequenceEqual(initialOrder)
                || !IsolatedLayerUnchanged()
                || FrontFillOwnerId() != frontId)
            {
                throw new InvalidOperationException(
                    "Scene-instance stack shortcut regression did not enter focused Scene Building 2D with the expected initial composition.");
            }

            SelectInstances([middleId, backId], backId);
            if (!InvokeShortcut(moveForward)
                || !CurrentStackOrder().SequenceEqual([middleId, backId, frontId])
                || !IsolatedLayerUnchanged()
                || FrontFillOwnerId() != middleId
                || !SelectionPreserved([middleId, backId], backId))
            {
                throw new InvalidOperationException(
                    "Ctrl+Up in Scene Building 2D did not stably move the selected instances forward, refresh composition, and preserve selection.");
            }
            if (!Undo()
                || !CurrentStackOrder().SequenceEqual(initialOrder)
                || !IsolatedLayerUnchanged()
                || FrontFillOwnerId() != frontId)
            {
                throw new InvalidOperationException(
                    "One Undo did not restore the Ctrl+Up scene-instance order and composed front owner.");
            }

            SelectInstances([frontId, middleId], middleId);
            if (!InvokeShortcut(moveBackward)
                || !CurrentStackOrder().SequenceEqual([backId, frontId, middleId])
                || !IsolatedLayerUnchanged()
                || FrontFillOwnerId() != backId
                || !SelectionPreserved([frontId, middleId], middleId))
            {
                throw new InvalidOperationException(
                    "Ctrl+Down in Scene Building 2D did not stably move the selected instances backward, refresh composition, and preserve selection.");
            }
            if (!Undo()
                || !CurrentStackOrder().SequenceEqual(initialOrder)
                || !IsolatedLayerUnchanged()
                || FrontFillOwnerId() != frontId)
            {
                throw new InvalidOperationException(
                    "One Undo did not restore the Ctrl+Down scene-instance order and composed front owner.");
            }

            SelectInstances([frontId], frontId);
            if (InvokeShortcut(moveForward)
                || !CurrentStackOrder().SequenceEqual(initialOrder)
                || !IsolatedLayerUnchanged()
                || FrontFillOwnerId() != frontId)
            {
                throw new InvalidOperationException(
                    "Ctrl+Up consumed or mutated an already-frontmost Scene Building 2D instance.");
            }
            SelectInstances([backId], backId);
            if (InvokeShortcut(moveBackward)
                || !CurrentStackOrder().SequenceEqual(initialOrder)
                || !IsolatedLayerUnchanged()
                || FrontFillOwnerId() != frontId)
            {
                throw new InvalidOperationException(
                    "Ctrl+Down consumed or mutated an already-backmost Scene Building 2D instance.");
            }

            sceneDefinition.Dimension = SceneDimension.ThreeD;
            bindSceneEditStage.Invoke(form, [false]);
            stage.Select();
            stage.Focus();
            Application.DoEvents();
            SelectInstances([middleId], middleId);
            if (stage.ReferenceDimension != SceneDimension.ThreeD
                || InvokeShortcut(moveForward)
                || InvokeShortcut(moveBackward)
                || !CurrentStackOrder().SequenceEqual(initialOrder)
                || !IsolatedLayerUnchanged()
                || !SelectionPreserved([middleId], middleId)
                || Undo())
            {
                throw new InvalidOperationException(
                    "Scene Building 3D accepted a 2D stack shortcut, changed selection/order, or created an undo entry.");
            }

            Console.WriteLine("scene_instance_stack_shortcuts=ok");
        }
        finally
        {
            projectDirtyField.SetValue(form, false);
            form.Hide();
        }
    }

    private static void RunDrawingTopologyRegression()
    {
        RunIncrementalAppendRegression();
        RunQueryActiveFrameRegression();
        RunBrushBatchAppendRegression();
        RunLocalizedSpatialAlgorithmRegression();
        RunFirstDragMovePerformanceRegression();
        RunFillOverwriteRegression();
        RunFastSelectionProbeRegression();
        RunCrossingFillTopologyRegression();
        RunTerminatingLineTopologyRegression();
        RunTerminatingStrokeContactRegression();
        RunCommittedTerminatingCurveSplitRegression();
        RunCrossingLinesTopologyRegression();
        RunCollinearOverlapTopologyRegression();
        RunOutlinedBoundaryTopologyRegression();
        RunSmoothPathBoundaryGroupingRegression();
        RunCrossLayerTopologyRegression();
        RunMultipleCutterFillTopologyRegression();
        RunCurvedCutterFillTopologyRegression();
        RunScaledClosedCutterFillTopologyRegression();
        RunCompoundFillTopologyRegression();
        RunPencilStrokeTopologyRegression();
        RunConnectedCutterNetworkTopologyRegression();
        RunConnectedStrokeSelectionRegression();
        RunConnectedLineRecolorRegression();
        RunMaterializedOrderTopologyRegression();
        RunBatchElementMaterializationRegression();
        RunTopologyCelOwnershipRegression();
        RunOutlinedFillMergeRegression();
        RunFillBoundaryOverlapNormalizationRegression();
        RunMarqueeElementQueryRegression();
        RunMarqueeLineMaterializationRegression();
        RunMarqueeFillMaterializationRegression();
        RunMovedFillIsolationRegression();
        RunMarqueeOutlinedBoundarySelectionRegression();
        RunLineToFillConversionRegression();
        RunConnectedLineBranchRegression();
        RunLineSegmentMergeRegression();
        Console.WriteLine("drawing_topology_regressions=37");
    }

    private static void RunTopologyCelOwnershipRegression()
    {
        var materializeScene = new VectorScene();
        materializeScene.CreateEmpty();
        var fill = AddTopologyFill(materializeScene, 0, 0);
        AddTopologyLine(materializeScene, 0, new PointF(-320, 0), new PointF(320, 0));
        if (materializeScene.GetFillParts(fill, 0).Length != 2
            || !materializeScene.InsertTimelineKeyframe(0, 10)
            || !materializeScene.InsertTimelineBlankKeyframe(0, 20))
        {
            throw new InvalidOperationException("Topology cel ownership setup failed.");
        }

        var materializeTrack = materializeScene.Timeline.FindTrackByTargetId(materializeScene.LayerIds[0])
            ?? throw new InvalidOperationException("Topology cel ownership setup lost its layer track.");
        var frameTenCount = Enumerable.Range(0, materializeScene.ObjectCount)
            .Count(index => materializeScene.ObjectKeyframeFrame[index] == 10);
        materializeScene.EditFrame = 20;
        var materialized = materializeScene.MaterializeSelectedParts(
            [new DrawingElementKey(fill, DrawingElementKind.Fill, 0)],
            frame: 0);
        if (!materialized.Success
            || !materialized.Changed
            || materialized.Parts.Length != 1
            || materialized.Parts.Any(part => materializeScene.ObjectKeyframeFrame[part.Result.ObjectIndex] != 0)
            || Enumerable.Range(0, materializeScene.ObjectCount)
                .Count(index => materializeScene.ObjectKeyframeFrame[index] == 10) != frameTenCount
            || Enumerable.Range(0, materializeScene.ObjectCount)
                .Any(index => materializeScene.ObjectKeyframeFrame[index] == 20)
            || materializeTrack.EvaluateExposure(20).SourceKind != TimelineKeyframeKind.Blank)
        {
            throw new InvalidOperationException("Topology materialization wrote replacements or keyframe content into EditFrame instead of the source cel.");
        }

        var mergeScene = new VectorScene();
        mergeScene.CreateEmpty();
        mergeScene.AddObject(
            0,
            new PointF(-30, 0),
            new SizeF(100, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        var second = mergeScene.AddObject(
            0,
            new PointF(30, 0),
            new SizeF(100, 80),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            12,
            ShapeKind.Rectangle);
        if (!mergeScene.InsertTimelineKeyframe(0, 10))
        {
            throw new InvalidOperationException("Fill merge cel ownership setup failed.");
        }

        frameTenCount = Enumerable.Range(0, mergeScene.ObjectCount)
            .Count(index => mergeScene.ObjectKeyframeFrame[index] == 10);
        mergeScene.EditFrame = 10;
        var merged = mergeScene.MergeSameColorFillsAround(second, connectNearby: false, frame: 0);
        if ((uint)merged >= mergeScene.ObjectCount
            || mergeScene.ObjectKeyframeFrame[merged] != 0
            || !mergeScene.IsObjectActive(merged, 0)
            || Enumerable.Range(0, mergeScene.ObjectCount)
                .Count(index => mergeScene.ObjectKeyframeFrame[index] == 10) != frameTenCount)
        {
            throw new InvalidOperationException("Fill merge wrote its replacement into EditFrame instead of the source cel.");
        }

        var cacheScene = new VectorScene();
        cacheScene.CreateEmpty();
        var cachedFill = AddTopologyFill(cacheScene, 0, 0);
        var cutter = AddTopologyLine(cacheScene, 0, new PointF(-320, 0), new PointF(320, 0));
        if (cacheScene.GetFillParts(cachedFill, 0).Length != 2)
        {
            throw new InvalidOperationException("Topology cache invalidation setup did not split its Fill.");
        }

        var movedStart = new PointF(-320, -200);
        var movedEnd = new PointF(320, -200);
        cacheScene.SetLineEndpoint(
            cutter,
            startEndpoint: true,
            movedStart,
            new PointF(320, 0),
            new PointF(0, -100),
            keepStraight: true);
        cacheScene.SetLineEndpoint(
            cutter,
            startEndpoint: false,
            movedEnd,
            movedStart,
            new PointF(0, -200),
            keepStraight: true);
        var refreshedParts = cacheScene.GetFillParts(cachedFill, 0);
        cacheScene.TryGetLineCubic(
            cutter,
            out var refreshedStart,
            out _,
            out _,
            out var refreshedEnd);
        if (refreshedParts.Length != 1)
        {
            throw new InvalidOperationException(
                $"Moving a cutter through the engine API reused a stale Fill partition: parts={refreshedParts.Length}, line={refreshedStart}->{refreshedEnd}.");
        }
    }

    private static void RunFastSelectionProbeRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.AddObject(
            0,
            PointF.Empty,
            new SizeF(200, 120),
            0,
            0,
            Color.Teal,
            Color.Transparent,
            8,
            ShapeKind.Rectangle);
        scene.AddLineSegment(
            0,
            new PointF(-160, 180),
            new PointF(160, 180),
            12,
            Color.Transparent,
            Color.White,
            8);
        if (!scene.HasSelectableObjectAt(PointF.Empty, 0, 4)
            || !scene.HasSelectableObjectAt(new PointF(0, 180), 0, 4)
            || scene.HasSelectableObjectAt(new PointF(400, 400), 0, 4))
        {
            throw new InvalidOperationException("The fast selection probe did not distinguish fills, strokes, and empty canvas space.");
        }

        const int denseLineCount = 4_000;
        var denseScene = new VectorScene();
        denseScene.CreateEmpty();
        denseScene.BeginDeferredAppend(denseLineCount, [0]);
        try
        {
            for (var index = 0; index < denseLineCount; index++)
            {
                var y = 100 + index % 3;
                denseScene.AppendCubicCurveSegment(
                    0,
                    new PointF(-140, y),
                    new PointF(-45, y),
                    new PointF(45, y),
                    new PointF(140, y),
                    4,
                    Color.Transparent,
                    Color.White,
                    6);
            }
        }
        finally
        {
            denseScene.EndDeferredAppend();
        }
        denseScene.CompleteDeferredBuild();

        const int samples = 64;
        _ = denseScene.HasSelectableObjectAt(PointF.Empty, 0, 4);
        var watch = Stopwatch.StartNew();
        for (var sample = 0; sample < samples; sample++)
        {
            if (denseScene.HasSelectableObjectAt(PointF.Empty, 0, 4))
            {
                throw new InvalidOperationException("The fast selection probe reported a distant dense stroke as a hit.");
            }
        }
        watch.Stop();
        var averageMilliseconds = watch.Elapsed.TotalMilliseconds / samples;
        const double budgetMilliseconds = 5;
        if (averageMilliseconds > budgetMilliseconds)
        {
            throw new InvalidOperationException(
                $"The fast blank-canvas selection probe exceeded its budget: averageMs={averageMilliseconds:0.000}.");
        }
        Console.WriteLine($"selection_probe_avg_ms={averageMilliseconds:0.000}");
        Console.WriteLine($"selection_probe_budget_ms={budgetMilliseconds:0.000}");
        Console.WriteLine("selection_probe_budget_met=true");
    }

    private static void RunIncrementalAppendRegression()
    {
        const int objectCount = 1_024;
        var scene = new VectorScene();
        scene.CreateEmpty();
        var appendWatch = Stopwatch.StartNew();
        for (var index = 0; index < objectCount; index++)
        {
            var x = -20_000 + (index * 97 % 180) * 220;
            var y = -10_000 + (index * 53 % 80) * 220;
            scene.AddObject(0, new PointF(x, y), new SizeF(48, 48), 0, 0, Color.Teal, Color.Transparent, 6, ShapeKind.Rectangle);
        }
        appendWatch.Stop();

        var allObjects = scene.QueryObjects(new RectangleF(-24_000, -14_000, 48_000, 28_000), 0);
        var indexedObjects = 0;
        for (var cell = 0; cell < scene.IndexColumnCount * scene.IndexRowCount; cell++)
        {
            indexedObjects += scene.GetSpatialCellObjectCount(cell);
        }

        if (indexedObjects != objectCount || !allObjects.SequenceEqual(Enumerable.Range(0, objectCount)))
        {
            throw new InvalidOperationException("Incremental append produced an incomplete or unstable spatial index.");
        }

        Console.WriteLine($"incremental_append_avg_ms={appendWatch.Elapsed.TotalMilliseconds / objectCount:0.000}");
        Console.WriteLine($"incremental_append_objects={objectCount}");
    }

    private static void RunQueryActiveFrameRegression()
    {
        var scene = new VectorScene();
        scene.CreateEmpty(layers: 2);
        var active = scene.AddObject(0, PointF.Empty, new SizeF(80, 80), 0, 0, Color.Teal, Color.Transparent, 6, ShapeKind.Rectangle);
        scene.AddObject(1, PointF.Empty, new SizeF(80, 80), 0, 0, Color.Coral, Color.Transparent, 6, ShapeKind.Rectangle);
        scene.SetLayerVisible(1, false);
        var bounds = new RectangleF(-100, -100, 200, 200);
        if (!scene.QueryObjects(bounds, 0).SequenceEqual([active])
            || !scene.SetLayerVisible(0, false)
            || scene.QueryObjects(bounds, 0).Length != 0
            || !scene.SetLayerVisible(0, true)
            || !scene.InsertTimelineBlankKeyframe(0, 5)
            || scene.QueryObjects(bounds, 5).Length != 0)
        {
            throw new InvalidOperationException("Spatial query did not honor layer visibility or the active keyframe exposure.");
        }

        Console.WriteLine("query_active_frame_cache=ok");
    }

    private static void RunBrushBatchAppendRegression()
    {
        var centerline = new[] { new PointF(-160, 0), PointF.Empty, new PointF(160, 0) };
        var brushShape = BrushShape.CreateSoftRound();
        var softScene = new VectorScene();
        softScene.CreateEmpty();
        var softGeometryRevision = softScene.GeometryRevision;
        var softSummaryRevision = softScene.SummaryRevision;
        var softObjects = softScene.AddSoftBrushStroke(
            0,
            centerline,
            VectorUnits.StrokePointsToUnits(16),
            Color.Coral,
            brushShape,
            32);
        if (softObjects.Length != brushShape.Layers.Count
            || softScene.GeometryRevision != softGeometryRevision + 1
            || softScene.SummaryRevision != softSummaryRevision + 1)
        {
            throw new InvalidOperationException("Soft brush layers did not share one final geometry and summary rebuild.");
        }

        var pressureScene = new VectorScene();
        pressureScene.CreateEmpty();
        var pressureGeometryRevision = pressureScene.GeometryRevision;
        var pressureSummaryRevision = pressureScene.SummaryRevision;
        var pressureObjects = pressureScene.AddPressureBrushStroke(
            0,
            [
                new PressureBrushSample(new PointF(-160, 0), 240, 0),
                new PressureBrushSample(PointF.Empty, 180, 0.2f),
                new PressureBrushSample(new PointF(160, 0), 240, 0.4f)
            ],
            VectorUnits.StrokePointsToUnits(16),
            Color.Coral,
            brushShape,
            32,
            smoothing: 58,
            simplifyTolerance: 1);
        if (pressureObjects.Length != brushShape.Layers.Count
            || pressureScene.GeometryRevision != pressureGeometryRevision + 1
            || pressureScene.SummaryRevision != pressureSummaryRevision + 1)
        {
            throw new InvalidOperationException("Pressure brush layers did not share one final geometry and summary rebuild.");
        }

        Console.WriteLine("brush_batch_append_rebuilds=ok");
    }

    private static void RunComplexBrushCommitPerformanceRegression()
    {
        const int existingStrokeCount = 64;
        const int samplesPerStroke = 96;
        var scene = new VectorScene();
        scene.CreateEmpty();
        var brushShape = BrushShape.CreateTraditionalBrush();
        var diameter = VectorUnits.StrokePointsToUnits(16);
        var stops = new[]
        {
            new GradientStop(0, Color.Teal),
            new GradientStop(0.5f, Color.Gold),
            new GradientStop(1, Color.MediumPurple)
        };
        var points = new PointF[samplesPerStroke];
        for (var stroke = 0; stroke < existingStrokeCount; stroke++)
        {
            var baseY = -1_600 + stroke * 50f;
            for (var sample = 0; sample < samplesPerStroke; sample++)
            {
                var x = -1_800 + sample * (3_600f / (samplesPerStroke - 1));
                points[sample] = new PointF(x, baseY + MathF.Sin(sample * 0.31f + stroke * 0.17f) * 34f);
            }

            var objects = scene.AddSoftBrushStroke(0, points, diameter, Color.Teal, brushShape, samplesPerStroke);
            if (objects.Length != 1) throw new InvalidOperationException("Complex brush commit setup did not create one traditional brush object.");
            scene.SetGradientPaint(objects[0], GradientKind.Linear, stops, points[0], points[^1]);
            scene.SetGradientPath(objects[0], points);
        }

        var commitPath = new PointF[256];
        for (var sample = 0; sample < commitPath.Length; sample++)
        {
            var amount = sample / (float)(commitPath.Length - 1);
            commitPath[sample] = new PointF(
                MathF.Sin(amount * MathF.Tau * 5f) * 620f,
                -1_720 + amount * 3_440f);
        }

        var watch = Stopwatch.StartNew();
        var stageWatch = Stopwatch.StartNew();
        var snapshot = scene.CreateSnapshot();
        stageWatch.Stop();
        var snapshotMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
        stageWatch.Restart();
        var committed = scene.AddSoftBrushStroke(
            0,
            commitPath,
            diameter,
            Color.Coral,
            brushShape,
            (uint)commitPath.Length);
        stageWatch.Stop();
        var geometryMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
        stageWatch.Restart();
        foreach (var objectIndex in committed)
        {
            scene.SetGradientPaint(objectIndex, GradientKind.Linear, stops, commitPath[0], commitPath[^1]);
            scene.SetGradientPath(objectIndex, commitPath);
        }
        stageWatch.Stop();
        var gradientMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
        stageWatch.Restart();
        var normalizeInteractively = scene.CanNormalizePaintInteractively(committed, 0);
        var retained = scene.NormalizePaintForInteractiveCommit(
            committed,
            enforceComplexityBudget: true);
        stageWatch.Stop();
        var overwriteMilliseconds = stageWatch.Elapsed.TotalMilliseconds;
        var mergeMilliseconds = 0d;
        watch.Stop();
        GC.KeepAlive(snapshot);

        const double budgetMilliseconds = 10;
        var budgetMet = committed.Length == 1
            && retained.Length == 1
            && !normalizeInteractively
            && watch.Elapsed.TotalMilliseconds <= budgetMilliseconds;
        Console.WriteLine($"complex_brush_commit_ms={watch.Elapsed.TotalMilliseconds:0.00}");
        Console.WriteLine($"complex_brush_snapshot_ms={snapshotMilliseconds:0.00}");
        Console.WriteLine($"complex_brush_geometry_ms={geometryMilliseconds:0.00}");
        Console.WriteLine($"complex_brush_gradient_ms={gradientMilliseconds:0.00}");
        Console.WriteLine($"complex_brush_overwrite_ms={overwriteMilliseconds:0.00}");
        Console.WriteLine($"complex_brush_merge_ms={mergeMilliseconds:0.00}");
        Console.WriteLine($"complex_brush_commit_budget_ms={budgetMilliseconds:0.00}");
        Console.WriteLine($"complex_brush_commit_budget_met={budgetMet.ToString().ToLowerInvariant()}");
        if (!budgetMet)
        {
            throw new InvalidOperationException(
                $"Complex brush commit exceeded its budget or lost the committed object: elapsed={watch.Elapsed.TotalMilliseconds:0.00}, committed={committed.Length}, retained={retained.Length}.");
        }

        var solidScene = new VectorScene();
        solidScene.CreateEmpty();
        var solidPath = new PointF[512];
        for (var sample = 0; sample < solidPath.Length; sample++)
        {
            var amount = sample / (float)(solidPath.Length - 1);
            solidPath[sample] = new PointF(
                -2_400 + amount * 4_800,
                MathF.Sin(amount * MathF.Tau * 8f) * 520f);
        }

        var firstSolid = solidScene.AddSoftBrushStroke(
            0,
            solidPath,
            diameter,
            Color.Coral,
            brushShape,
            (uint)solidPath.Length);
        var shiftedSolidPath = solidPath.Select(point => new PointF(point.X, point.Y + 30)).ToArray();
        var processedSolidPath = FreehandStrokeProcessor.Process(
            shiftedSolidPath,
            smoothing: 12,
            simplifyTolerance: VectorUnits.FromPixels(0.9f));
        var solidWatch = Stopwatch.StartNew();
        var solidSnapshot = solidScene.CreateSnapshot();
        var secondSolid = solidScene.AddSoftBrushStroke(
            0,
            processedSolidPath,
            diameter,
            Color.Coral,
            brushShape,
            (uint)processedSolidPath.Length);
        var solidNormalizeInteractively = solidScene.CanNormalizePaintInteractively(secondSolid, 0);
        var solidMerged = solidScene.NormalizePaintForInteractiveCommit(
            secondSolid,
            enforceComplexityBudget: true);
        solidWatch.Stop();
        GC.KeepAlive(solidSnapshot);

        const double solidBudgetMilliseconds = 10;
        var solidBudgetMet = firstSolid.Length == 1
            && secondSolid.Length == 1
            && solidMerged.Length == 1
            && !solidNormalizeInteractively
            && solidScene.ObjectCount == 2
            && solidWatch.Elapsed.TotalMilliseconds <= solidBudgetMilliseconds;
        Console.WriteLine($"complex_solid_brush_commit_ms={solidWatch.Elapsed.TotalMilliseconds:0.00}");
        Console.WriteLine($"complex_solid_brush_commit_budget_ms={solidBudgetMilliseconds:0.00}");
        Console.WriteLine($"complex_solid_brush_commit_budget_met={solidBudgetMet.ToString().ToLowerInvariant()}");
        if (!solidBudgetMet)
        {
            throw new InvalidOperationException(
                $"Complex solid brush commit exceeded its budget or failed to merge: elapsed={solidWatch.Elapsed.TotalMilliseconds:0.00}, objects={solidScene.ObjectCount}, merged={solidMerged.Length}.");
        }

        const int longSoftBrushSamples = 1_024;
        var longSoftBrushPath = new PointF[longSoftBrushSamples];
        for (var sample = 0; sample < longSoftBrushPath.Length; sample++)
        {
            var amount = sample / (float)(longSoftBrushPath.Length - 1);
            longSoftBrushPath[sample] = new PointF(
                -25_000 + amount * 50_000,
                MathF.Sin(amount * MathF.Tau * 18f) * 750f);
        }
        var processedLongSoftBrushPath = FreehandStrokeProcessor.Process(
            longSoftBrushPath,
            smoothing: 64,
            simplifyTolerance: VectorUnits.FromPixels(0.9f));
        var longSoftBrushScene = new VectorScene();
        longSoftBrushScene.CreateEmpty();
        var longSoftBrushShape = BrushShape.CreateSoftRound();
        var longSoftBrushWatch = Stopwatch.StartNew();
        var longSoftBrushObjects = longSoftBrushScene.AddSoftBrushStroke(
            0,
            processedLongSoftBrushPath,
            diameter,
            Color.Coral,
            longSoftBrushShape,
            (uint)processedLongSoftBrushPath.Length);
        var longSoftBrushRetained = longSoftBrushScene.NormalizePaintForInteractiveCommit(
            longSoftBrushObjects,
            enforceComplexityBudget: true);
        longSoftBrushWatch.Stop();

        const double longSoftBrushBudgetMilliseconds = 30;
        var longSoftBrushBudgetMet = longSoftBrushObjects.Length == longSoftBrushShape.Layers.Count
            && longSoftBrushRetained.Length == longSoftBrushObjects.Length
            && longSoftBrushWatch.Elapsed.TotalMilliseconds <= longSoftBrushBudgetMilliseconds
            && !StageControl.ShouldSynchronouslyPresentInteractiveFrame(
                forceFinalFrame: true,
                presentationDeferred: true,
                hasPresentedFrame: true,
                elapsedSinceLastFrameMilliseconds: 100)
            && StageControl.ShouldSynchronouslyPresentInteractiveFrame(
                forceFinalFrame: true,
                presentationDeferred: false,
                hasPresentedFrame: true,
                elapsedSinceLastFrameMilliseconds: 0);
        Console.WriteLine($"long_soft_brush_retained_points={processedLongSoftBrushPath.Length}");
        Console.WriteLine($"long_soft_brush_commit_ms={longSoftBrushWatch.Elapsed.TotalMilliseconds:0.00}");
        Console.WriteLine($"long_soft_brush_commit_budget_ms={longSoftBrushBudgetMilliseconds:0.00}");
        Console.WriteLine($"long_soft_brush_commit_budget_met={longSoftBrushBudgetMet.ToString().ToLowerInvariant()}");
        if (!longSoftBrushBudgetMet)
        {
            throw new InvalidOperationException(
                $"Long soft brush commit exceeded its budget or forced a synchronous release frame: elapsed={longSoftBrushWatch.Elapsed.TotalMilliseconds:0.00}, points={processedLongSoftBrushPath.Length}, objects={longSoftBrushObjects.Length}, retained={longSoftBrushRetained.Length}.");
        }
    }

    private static void RunLocalizedSpatialAlgorithmRegression()
    {
        const int distantObjectCount = 8_192;
        var scene = new VectorScene();
        scene.CreateEmpty();
        scene.BeginDeferredAppend(distantObjectCount + 2, [0]);
        int nearFirst;
        int nearSecond;
        try
        {
            for (var index = 0; index < distantObjectCount; index++)
            {
                scene.AppendObject(
                    0,
                    new PointF(-22_000 + index % 128 * 100, -12_000 + index / 128 * 100),
                    new SizeF(40, 40),
                    0,
                    0,
                    Color.Teal,
                    Color.Transparent,
                    6,
                    ShapeKind.Rectangle);
            }

            nearFirst = scene.AppendObject(0, PointF.Empty, new SizeF(96, 96), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
            nearSecond = scene.AppendObject(0, new PointF(20, 0), new SizeF(96, 96), 0, 0, Color.Teal, Color.Transparent, 12, ShapeKind.Rectangle);
        }
        finally
        {
            scene.EndDeferredAppend();
        }
        scene.CompleteDeferredBuild();

        var localBounds = new RectangleF(-120, -120, 240, 240);
        var localObjects = scene.QueryObjects(localBounds, 0);
        if (!localObjects.SequenceEqual([nearFirst, nearSecond]))
        {
            throw new InvalidOperationException("Spatial query did not return the unique local objects in stable order.");
        }

        const int querySamples = 512;
        var queryWatch = Stopwatch.StartNew();
        for (var sample = 0; sample < querySamples; sample++)
        {
            var result = scene.QueryObjects(localBounds, 0);
            if (result.Length != 2) throw new InvalidOperationException("Repeated local spatial query changed its result.");
        }
        queryWatch.Stop();

        var mergeWatch = Stopwatch.StartNew();
        var merged = scene.MergeSameColorFillsAround(nearSecond, connectNearby: false, frame: 0);
        mergeWatch.Stop();
        if (scene.ObjectCount != distantObjectCount + 1
            || (uint)merged >= scene.ObjectCount
            || !scene.FillContainsPoint(merged, PointF.Empty))
        {
            throw new InvalidOperationException("Localized fill merge changed distant objects or lost its merged region.");
        }

        var renderOrder = new SceneRenderOrderBuffer();
        renderOrder.Collect(scene, localBounds, 0);
        if (renderOrder.VisibleCount != 1
            || !renderOrder.GetLayerObjects(0).SequenceEqual([merged]))
        {
            throw new InvalidOperationException("Render collection retained a removed fill after localized compaction.");
        }

        renderOrder.Collect(scene, new RectangleF(-24_000, -14_000, 48_000, 28_000), 0);
        if (renderOrder.VisibleCount != scene.ObjectCount)
        {
            throw new InvalidOperationException("Render collection lost live objects while skipping compacted spatial entries.");
        }

        Console.WriteLine($"local_spatial_query_avg_ms={queryWatch.Elapsed.TotalMilliseconds / querySamples:0.000}");
        Console.WriteLine($"local_fill_merge_ms={mergeWatch.Elapsed.TotalMilliseconds:0.000}");
        Console.WriteLine($"local_fill_merge_plan_ms={scene.LastFillMergePlanMilliseconds:0.000}");
        Console.WriteLine($"local_fill_merge_snapshot_ms={scene.LastFillMergeSnapshotMilliseconds:0.000}");
        Console.WriteLine($"local_fill_merge_mutation_ms={scene.LastFillMergeMutationMilliseconds:0.000}");
        Console.WriteLine($"local_fill_merge_spatial_ms={scene.LastFillMergeSpatialMilliseconds:0.000}");
        Console.WriteLine($"local_fill_merge_summary_ms={scene.LastFillMergeSummaryMilliseconds:0.000}");
        Console.WriteLine($"local_fill_merge_distant_objects={distantObjectCount}");
    }

}
