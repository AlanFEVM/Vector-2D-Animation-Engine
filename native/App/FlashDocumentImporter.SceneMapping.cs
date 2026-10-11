using System.Drawing;
using System.Globalization;
using System.Xml.Linq;

namespace VectorAnimationEngine;

internal static partial class FlashDocumentImporter
{
    private static Color ReadLayerColor(XElement layer, string location)
    {
        var value = Required(layer, "color");
        if (value.Length != 7 || value[0] != '#'
            || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, Invariant, out var rgb))
            throw Failure(location, "Layer color must be #RRGGBB.");
        return Color.FromArgb(unchecked((int)(0xff000000 | rgb)));
    }

    private static Dictionary<string, string> ImportLibraryFolders(
        VectorProject project, XElement document, IEnumerable<string> symbolNames, List<FlashImportDiagnostic> diagnostics)
    {
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var folder in Children(Child(document, "folders"), "DOMFolderItem")) Ensure(Required(folder, "name"));
        foreach (var name in symbolNames)
        {
            var parts = Parts(name);
            if (parts.Length > 1) Ensure(string.Join('/', parts[..^1]));
        }
        return folders;

        void Ensure(string path)
        {
            var parts = Parts(path);
            var parent = "";
            var current = "";
            foreach (var part in parts)
            {
                current = current.Length == 0 ? part : current + "/" + part;
                if (!folders.TryGetValue(current, out var id))
                {
                    if (folders.Count >= MaximumSymbols
                        || !project.TryAddAssetFolder(part, parent, out var folder) || folder is null)
                        throw Failure(path, "Too many or invalid library folders.");
                    if (part != folder.Name)
                        diagnostics.Add(new("library-folder-name-normalized", current,
                            "Library folder whitespace was normalized by the existing V2D naming rules; source symbol references remain distinct."));
                    folders.Add(current, id = folder.Id);
                }
                parent = id;
            }
        }

        static string[] Parts(string path)
        {
            var parts = path.Split('/');
            if (parts.Length > 64 || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
                throw Failure(path, "Invalid library folder or symbol path.");
            return parts;
        }
    }

    private static void RestoreNativeScene(VectorProject project, SceneDefinition scene,
        DrawingObjectDefinition staging, decimal fps, List<FlashImportDiagnostic> diagnostics)
    {
        var source = staging.Scene;
        var timeline = staging.Timeline.CreateSnapshot();
        var roots = Enumerable.Range(0, source.LayerCount).Where(layer => source.GetLayerParentIndex(layer) < 0).ToArray();
        var layers = new List<SceneLayerSnapshotItem>();
        var tracks = new List<AnimationTimelineTrackSnapshot>();
        var groups = new List<(int Root, int[] Members, string LayerId, bool Wrapped)>();
        foreach (var root in roots)
        {
            var members = source.ResolveLayerRemovalIndices([source.LayerIds[root]]);
            var layerId = Guid.NewGuid().ToString("N");
            var wrapped = members.Length > 1 || source.GetLayerKind(root) == DrawingLayerKind.Folder || source.LayerLocked[root];
            groups.Add((root, members, layerId, wrapped));
            layers.Add(new(layerId, source.LayerNames[root], source.LayerVisible[root],
                source.LayerColorArgb[root], source.LayerOutline[root] && source.GetLayerKind(root) != DrawingLayerKind.Folder));
            var sourceTracks = members.Where(member => source.GetLayerKind(member) != DrawingLayerKind.Folder)
                .Select(member => timeline.Tracks.Single(track => track.TargetId == source.LayerIds[member])).ToArray();
            // Folder rows have no drawable Cel of their own. Their scene row displays the
            // union of the children's source keys while the editable group retains each track.
            var keyFrames = sourceTracks.SelectMany(track => track.Keyframes.Select(key => key.Frame)).Append(0).Distinct().Order().ToArray();
            var keys = keyFrames.Select(frame => new TimelineKeyframe(frame,
                sourceTracks.Any(track => HasContent(track, frame)) ? TimelineKeyframeKind.Populated : TimelineKeyframeKind.Blank)).ToArray();
            tracks.Add(new() { Id = Guid.NewGuid().ToString("N"), TargetId = layerId, Duration = staging.FrameCount, Keyframes = keys });
        }
        scene.RestoreLayerSnapshot(new() { Layers = layers.ToArray(), ActiveLayerId = layers[0].Id });
        foreach (var (root, members, layerId, wrapped) in groups)
        {
            if (!wrapped)
            {
                // Directly expose ordinary source instances on the scene timeline. Library
                // definitions are shared rather than copied for each placement or keyframe.
                foreach (var original in staging.InstancesInLayer(source.LayerIds[root]))
                {
                    var instance = original.Clone();
                    instance.SceneLayerId = layerId;
                    scene.AddInstance(project, instance);
                }
                continue;
            }
            var group = project.AddDrawingObject(scene.Name + " / " + source.LayerNames[root]);
            group.Detail = "Imported XFL scene layer group; edit this symbol to change its source layer properties and timeline.";
            // ImportBuilder represents every visual leaf as a referenced symbol. Copy only
            // this group's layer metadata and placements, rather than repeatedly cloning the
            // complete scene and its potentially large collection of baked frame states.
            group.Scene.CreateEmpty(members.Length, staging.FrameCount);
            for (var index = 0; index < members.Length; index++)
            {
                var member = members[index];
                group.Scene.LayerIds[index] = source.LayerIds[member];
                group.Scene.LayerNames[index] = source.LayerNames[member];
                group.Scene.LayerKinds[index] = source.LayerKinds[member];
                group.Scene.LayerParentIds[index] = source.LayerParentIds[member];
                group.Scene.LayerLocked[index] = source.LayerLocked[member];
                group.Scene.LayerVisible[index] = source.LayerVisible[member];
                group.Scene.LayerColorArgb[index] = source.LayerColorArgb[member];
                group.Scene.LayerOutline[index] = source.LayerOutline[member];
            }
            var keep = members.Select(member => source.LayerIds[member]).ToHashSet(StringComparer.Ordinal);
            group.RestoreInstanceSnapshot(members.SelectMany(member => staging.InstancesInLayer(source.LayerIds[member])));
            // Attach external instance content before restoring populated track kinds.
            group.Timeline.RestoreSnapshot(new()
            {
                Tracks = timeline.Tracks.Where(track => keep.Contains(track.TargetId)).ToArray(),
                TabGroups = timeline.TabGroups,
                ActiveTabGroupId = timeline.ActiveTabGroupId
            });
            group.SynchronizeTimelineTracks();
            var groupRoot = Array.IndexOf(group.Scene.LayerIds, source.LayerIds[root]);
            // Visibility belongs to the scene. A folder's outline remains inside the
            // group so descendants retain their individual outline colors; an outer
            // inherited outline would force every child to the scene row's color.
            group.Scene.LayerVisible[groupRoot] = true;
            if (source.GetLayerKind(root) != DrawingLayerKind.Folder) group.Scene.LayerOutline[groupRoot] = false;
            if (!project.TryAddSceneInstance(scene.Id, group.Id, PointF.Empty, 0, layerId, out var instanceRoot) || instanceRoot is null)
                throw Failure(scene.Name, "Could not create the native layer group instance.");
            instanceRoot.Name = source.LayerNames[root];
            instanceRoot.PlaybackFps = fps;
            instanceRoot.PlaybackMode = DrawingObjectPlaybackMode.PlayOnce;
            diagnostics.Add(new("scene-layer-group", scene.Name + "/" + source.LayerNames[root],
                "Scene layers do not have folder/lock fields. The source hierarchy, lock flags and child timelines are preserved in this editable V2D group symbol."));
        }
        scene.Timeline.RestoreSnapshot(new() { Tracks = tracks.ToArray() });
        scene.SynchronizeTimelineTracks();
        // This was an import staging definition, not an additional source library symbol.
        // A wholly empty document still retains the model's required last drawing object.
        project.TryRemoveDrawingObject(staging.Id, out _);

        static bool HasContent(AnimationTimelineTrackSnapshot track, int frame)
        {
            if (frame >= track.Duration) return false;
            var lower = 0;
            var upper = track.Keyframes.Length;
            while (lower < upper)
            {
                var middle = lower + (upper - lower) / 2;
                if (track.Keyframes[middle].Frame <= frame) lower = middle + 1;
                else upper = middle;
            }
            return lower > 0 && track.Keyframes[lower - 1].Kind == TimelineKeyframeKind.Populated;
        }
    }
}
