using System.Text.Json;

namespace VectorAnimationEngine;

internal static class MaterialPaletteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static GradientPreset[] LoadSavedGradients()
    {
        var path = StoragePath();
        if (!File.Exists(path)) return [];

        try
        {
            return Deserialize(File.ReadAllText(path));
        }
        catch
        {
            return [];
        }
    }

    public static void SaveSavedGradients(IEnumerable<GradientPreset> presets)
    {
        var path = StoragePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, Serialize(presets));
        File.Move(temporaryPath, path, overwrite: true);
    }

    internal static string Serialize(IEnumerable<GradientPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        var document = new MaterialPaletteDocument
        {
            SavedGradients = presets
                .Where(preset => preset.Kind != GradientKind.Solid)
                .Select(preset => new SavedGradient
                {
                    Name = preset.Name,
                    Kind = preset.Kind,
                    Stops = preset.Stops.ToArray()
                })
                .ToList()
        };
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    internal static GradientPreset[] Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var document = JsonSerializer.Deserialize<MaterialPaletteDocument>(json);
            return document?.SavedGradients?
                .Where(item => !string.IsNullOrWhiteSpace(item.Name)
                    && item.Kind != GradientKind.Solid
                    && item.Stops is { Length: >= 2 })
                .Select(item => new GradientPreset(item.Name, item.Kind, item.Stops, isUserSaved: true))
                .ToArray()
                ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string StoragePath() => Path.Combine(Directory.GetCurrentDirectory(), "data", "material-palettes.json");

    private sealed class MaterialPaletteDocument
    {
        public List<SavedGradient> SavedGradients { get; set; } = [];
    }

    private sealed class SavedGradient
    {
        public string Name { get; set; } = string.Empty;
        public GradientKind Kind { get; set; }
        public GradientStop[] Stops { get; set; } = [];
    }
}
