namespace VectorAnimationEngine;

internal static partial class Benchmark
{
    private static void RunAssetTagRegression()
    {
        const string characterTagId = "asset-tag-character";
        const string effectsTagId = "asset-tag-effects";
        var characterColor = Color.FromArgb(120, 214, 72, 88);
        var effectsColor = Color.FromArgb(96, 52, 136, 224);
        var project = VectorProject.CreateEmpty();
        var source = project.DrawingObjects[0];
        var unrelated = project.AddDrawingObject("Untagged Asset");
        var changedCount = 0;
        project.Changed += (_, _) => changedCount++;

        var initialTags = new[]
        {
            new ProjectAssetTagData(characterTagId, "Character", characterColor.ToArgb()),
            new ProjectAssetTagData(effectsTagId, "Effects", effectsColor.ToArgb())
        };
        AssertTimeline(
            project.TryApplyAssetTagEdit(source.Id, initialTags, [effectsTagId, characterTagId])
            && changedCount == 1
            && project.AssetTags.Count == 2
            && project.AssetTags[0].Id == characterTagId
            && project.AssetTags[0].ColorArgb == Color.FromArgb(255, characterColor.R, characterColor.G, characterColor.B).ToArgb()
            && project.AssetTags[1].Id == effectsTagId
            && source.AssetTagIds.SequenceEqual([characterTagId, effectsTagId], StringComparer.Ordinal)
            && unrelated.AssetTagIds.Count == 0,
            "Asset-tag creation did not normalize colors or assign tags in project order.");

        AssertTimeline(
            project.TryDuplicateDrawingObject(source.Id, out var duplicate)
            && duplicate is not null
            && duplicate.AssetTagIds.SequenceEqual(source.AssetTagIds, StringComparer.Ordinal),
            "Duplicating a drawing object did not preserve its asset-tag identifiers.");
        var duplicatedAsset = duplicate!;

        var renamedCharacterColor = Color.FromArgb(40, 236, 152, 38);
        var recoloredEffects = Color.FromArgb(180, 36, 196, 164);
        var revisedTags = new[]
        {
            new ProjectAssetTagData(characterTagId, "Hero", renamedCharacterColor.ToArgb()),
            new ProjectAssetTagData(effectsTagId, "Motion FX", recoloredEffects.ToArgb())
        };
        AssertTimeline(
            project.TryApplyAssetTagEdit(source.Id, revisedTags, source.AssetTagIds)
            && project.AssetTags[0].Name == "Hero"
            && project.AssetTags[0].ColorArgb == Color.FromArgb(255, renamedCharacterColor.R, renamedCharacterColor.G, renamedCharacterColor.B).ToArgb()
            && project.AssetTags[1].Name == "Motion FX"
            && project.AssetTags[1].ColorArgb == Color.FromArgb(255, recoloredEffects.R, recoloredEffects.G, recoloredEffects.B).ToArgb(),
            "Renaming or recoloring an asset tag did not update its stable definition.");

        var definitionsBeforeInvalidEdit = project.AssetTags
            .Select(tag => new ProjectAssetTagData(tag.Id, tag.Name, tag.ColorArgb))
            .ToArray();
        var sourceAssignmentsBeforeInvalidEdit = source.AssetTagIds.ToArray();
        var duplicateAssignmentsBeforeInvalidEdit = duplicatedAsset.AssetTagIds.ToArray();
        var changesBeforeInvalidEdit = changedCount;
        var invalidTags = new[]
        {
            new ProjectAssetTagData(characterTagId, "Duplicate", Color.Red.ToArgb()),
            new ProjectAssetTagData(effectsTagId, "duplicate", Color.Blue.ToArgb())
        };
        AssertTimeline(
            !project.TryApplyAssetTagEdit(source.Id, invalidTags, [characterTagId])
            && changedCount == changesBeforeInvalidEdit
            && project.AssetTags.Select(tag => new ProjectAssetTagData(tag.Id, tag.Name, tag.ColorArgb))
                .SequenceEqual(definitionsBeforeInvalidEdit)
            && source.AssetTagIds.SequenceEqual(sourceAssignmentsBeforeInvalidEdit, StringComparer.Ordinal)
            && duplicatedAsset.AssetTagIds.SequenceEqual(duplicateAssignmentsBeforeInvalidEdit, StringComparer.Ordinal),
            "An invalid asset-tag edit partially changed definitions, assignments, or notifications.");

        var retainedTag = revisedTags[1];
        AssertTimeline(
            project.TryApplyAssetTagEdit(source.Id, [retainedTag], [effectsTagId])
            && project.AssetTags.Count == 1
            && project.AssetTags[0].Id == effectsTagId
            && source.AssetTagIds.SequenceEqual([effectsTagId], StringComparer.Ordinal)
            && duplicatedAsset.AssetTagIds.SequenceEqual([effectsTagId], StringComparer.Ordinal),
            "Deleting an asset tag did not remove its assignments from every drawing object.");

        var restartRestored = VectorProject.RestoreRestartSnapshot(
            EditorRestartStore.RoundTripProjectSnapshot(project.CreateRestartSnapshot()));
        AssertAssetTagPersistence(
            restartRestored,
            source.Id,
            duplicatedAsset.Id,
            effectsTagId,
            "Editor restart JSON");

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "Vector2DAnimationEngine",
            $"asset-tag-regression-{Guid.NewGuid():N}");
        var manifestPath = Path.Combine(temporaryRoot, "AssetTags.v2dProject");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            ProjectVaultStore.Save(project, manifestPath);
            var vaultRestored = ProjectVaultStore.Load(manifestPath);
            AssertAssetTagPersistence(
                vaultRestored,
                source.Id,
                duplicatedAsset.Id,
                effectsTagId,
                "Project Vault");
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
            }
            catch
            {
                // Each regression run uses an isolated temporary directory.
            }
        }

        var coralTint = TimelineStrip.LayerItemBackgroundColor(Color.Coral, active: false, selected: false, alternate: false);
        var tealTint = TimelineStrip.LayerItemBackgroundColor(Color.Teal, active: false, selected: false, alternate: false);
        var activeTint = TimelineStrip.LayerItemBackgroundColor(Color.Coral, active: true, selected: false, alternate: false);
        var selectedTint = TimelineStrip.LayerItemBackgroundColor(Color.Coral, active: false, selected: true, alternate: false);
        var alternateTint = TimelineStrip.LayerItemBackgroundColor(Color.Coral, active: false, selected: false, alternate: true);
        AssertTimeline(
            coralTint.A == 255
            && tealTint.A == 255
            && activeTint.A == 255
            && selectedTint.A == 255
            && alternateTint.A == 255
            && coralTint.ToArgb() != tealTint.ToArgb()
            && activeTint.ToArgb() != coralTint.ToArgb()
            && selectedTint.ToArgb() != coralTint.ToArgb()
            && alternateTint.ToArgb() != coralTint.ToArgb(),
            "Timeline layer-item colors did not produce opaque, state-aware full-row tints.");

        var rowTint = Theme.Mix(Theme.Panel, Color.Coral, 0.28f);
        var hoverMidpoint = Theme.AdvanceRowMotion(0f, 1f, 55d, 110d);
        var hoverComplete = Theme.AdvanceRowMotion(0f, 1f, 110d, 110d);
        var reverseMidpoint = Theme.AdvanceRowMotion(1f, 0f, 55d, 110d);
        var hoverBackground = Theme.AnimatedRowBackgroundColor(rowTint, 1f, 0f, 0f);
        var selectedBackground = Theme.AnimatedRowBackgroundColor(rowTint, 1f, 1f, 0f);
        var pressedBackground = Theme.AnimatedRowBackgroundColor(rowTint, 0f, 1f, 1f);
        AssertTimeline(
            hoverMidpoint > 0f
            && hoverMidpoint < 1f
            && hoverComplete == 1f
            && reverseMidpoint > 0f
            && reverseMidpoint < 1f
            && hoverBackground == Theme.Mix(rowTint, Theme.PanelHover, 0.68f)
            && selectedBackground == Theme.Mix(rowTint, Theme.AccentSurface, 0.72f)
            && pressedBackground != selectedBackground
            && hoverBackground != rowTint,
            "Asset-library row motion did not preserve bounded easing and distinct hover, selected, or pressed states.");

        Console.WriteLine("asset_tag_regression=ok");
    }

    private static void AssertAssetTagPersistence(
        VectorProject restored,
        string sourceId,
        string duplicateId,
        string expectedTagId,
        string context)
    {
        var restoredSource = restored.DrawingObjects.SingleOrDefault(item => item.Id == sourceId);
        var restoredDuplicate = restored.DrawingObjects.SingleOrDefault(item => item.Id == duplicateId);
        AssertTimeline(
            restored.AssetTags.Count == 1
            && restored.AssetTags[0].Id == expectedTagId
            && restored.AssetTags[0].Name == "Motion FX"
            && restored.AssetTags[0].ColorArgb == Color.FromArgb(255, 36, 196, 164).ToArgb()
            && restoredSource is not null
            && restoredDuplicate is not null
            && restoredSource.AssetTagIds.SequenceEqual([expectedTagId], StringComparer.Ordinal)
            && restoredDuplicate.AssetTagIds.SequenceEqual([expectedTagId], StringComparer.Ordinal),
            $"{context} did not preserve asset-tag definitions and drawing-object assignments.");
    }
}
