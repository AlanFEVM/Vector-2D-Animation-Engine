using System.Text.Json;

namespace VectorAnimationEngine;

internal static class MinecraftLightMaterials
{
    internal const string Torch = "minecraft:torch";
    internal const int TorchEmission = 14;

    // Color values retained from the legacy section importer.
    private static readonly IReadOnlyDictionary<string, uint> GrassBiomeColors = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["plains"] = 0xff91bd59, ["sunflower_plains"] = 0xff91bd59, ["beach"] = 0xff91bd59,
        ["savanna"] = 0xffbfb755, ["savanna_plateau"] = 0xffbfb755, ["windswept_savanna"] = 0xffbfb755, ["desert"] = 0xffbfb755,
        ["jungle"] = 0xff59c93c, ["sparse_jungle"] = 0xff64c73f, ["bamboo_jungle"] = 0xff59c93c,
        ["forest"] = 0xff79c05a, ["flower_forest"] = 0xff79c05a, ["birch_forest"] = 0xff88bb67,
        ["old_growth_birch_forest"] = 0xff88bb67, ["dark_forest"] = 0xff507a32,
        ["taiga"] = 0xff86b783, ["old_growth_pine_taiga"] = 0xff86b87f, ["old_growth_spruce_taiga"] = 0xff8ab689,
        ["snowy_plains"] = 0xff80b497, ["ice_spikes"] = 0xff80b497, ["snowy_taiga"] = 0xff80b497,
        ["snowy_slopes"] = 0xff80b497, ["frozen_ocean"] = 0xff80b497, ["frozen_peaks"] = 0xff80b497,
        ["frozen_river"] = 0xff80b497, ["jagged_peaks"] = 0xff80b497,
        ["snowy_beach"] = 0xff83b593, ["river"] = 0xff71a74d, ["swamp"] = 0xff6a7039,
        ["mangrove_swamp"] = 0xff6a7039, ["badlands"] = 0xff90814d, ["wooded_badlands"] = 0xff9e814d,
        ["eroded_badlands"] = 0xff90814d, ["meadow"] = 0xff83bb6d, ["cherry_grove"] = 0xff7bbd65,
        ["grove"] = 0xff80b497, ["windswept_hills"] = 0xff8ab689, ["stony_shore"] = 0xff8ab689,
        ["stony_peaks"] = 0xff9abe4b, ["windswept_forest"] = 0xff8ab689, ["windswept_gravelly_hills"] = 0xff8ab689,
        ["lush_caves"] = 0xff298c4c, ["dripstone_caves"] = 0xff91bd59, ["deep_dark"] = 0xff91bd59,
        ["sulfur_caves"] = 0xffaba64f, ["mushroom_fields"] = 0xff55c93f, ["pale_garden"] = 0xff778272,
        ["ocean"] = 0xff8eb971, ["cold_ocean"] = 0xff8eb971, ["lukewarm_ocean"] = 0xff8eb971,
        ["warm_ocean"] = 0xff8eb971, ["deep_ocean"] = 0xff8eb971, ["deep_cold_ocean"] = 0xff8eb971,
        ["deep_lukewarm_ocean"] = 0xff8eb971, ["deep_frozen_ocean"] = 0xff8eb971,
        ["nether_wastes"] = 0xffbfb755, ["basalt_deltas"] = 0xffbfb755, ["crimson_forest"] = 0xffbfb755,
        ["soul_sand_valley"] = 0xffbfb755, ["warped_forest"] = 0xffbfb755, ["the_end"] = 0xff8eb971,
        ["end_barrens"] = 0xff8eb971, ["end_highlands"] = 0xff8eb971, ["end_midlands"] = 0xff8eb971,
        ["small_end_islands"] = 0xff8eb971, ["the_void"] = 0xff8eb971
    };

    private static readonly IReadOnlyDictionary<string, uint> FoliageBiomeColors = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["plains"] = 0xff77ab2f, ["sunflower_plains"] = 0xff77ab2f, ["savanna"] = 0xffaea42a,
        ["savanna_plateau"] = 0xffaea42a, ["windswept_savanna"] = 0xffaea42a,
        ["jungle"] = 0xff30bb0b, ["sparse_jungle"] = 0xff30bb0b, ["bamboo_jungle"] = 0xff30bb0b,
        ["forest"] = 0xff59ae30, ["flower_forest"] = 0xff59ae30, ["birch_forest"] = 0xff6a9c39,
        ["old_growth_birch_forest"] = 0xff6a9c39, ["dark_forest"] = 0xff3a6a1f,
        ["taiga"] = 0xff68a464, ["old_growth_pine_taiga"] = 0xff68a464, ["old_growth_spruce_taiga"] = 0xff6da36b,
        ["snowy_plains"] = 0xff60c17a, ["ice_spikes"] = 0xff60c17a, ["snowy_taiga"] = 0xff60c17a,
        ["frozen_ocean"] = 0xff60a17b, ["frozen_peaks"] = 0xff60a17b, ["frozen_river"] = 0xff60a17b,
        ["jagged_peaks"] = 0xff60a17b, ["swamp"] = 0xff6a7039, ["mangrove_swamp"] = 0xff6a7039,
        ["river"] = 0xff71a74d, ["badlands"] = 0xff9e814d, ["wooded_badlands"] = 0xff9e814d,
        ["meadow"] = 0xff83bb6d, ["cherry_grove"] = 0xff7bbd65, ["grove"] = 0xff60a17b,
        ["snowy_slopes"] = 0xff60a17b, ["stony_shore"] = 0xff6da36b, ["stony_peaks"] = 0xff82ac1e,
        ["windswept_forest"] = 0xff6da36b, ["windswept_gravelly_hills"] = 0xff6da36b,
        ["lush_caves"] = 0xff298c4c, ["dripstone_caves"] = 0xff77ab2f, ["deep_dark"] = 0xff77ab2f,
        ["sulfur_caves"] = 0xff77ab2f, ["mushroom_fields"] = 0xff2bbb0f, ["pale_garden"] = 0xff778272,
        ["ocean"] = 0xff71a74d, ["cold_ocean"] = 0xff71a74d, ["lukewarm_ocean"] = 0xff71a74d,
        ["warm_ocean"] = 0xff71a74d, ["deep_ocean"] = 0xff71a74d, ["deep_cold_ocean"] = 0xff71a74d,
        ["deep_lukewarm_ocean"] = 0xff71a74d, ["deep_frozen_ocean"] = 0xff71a74d,
        ["nether_wastes"] = 0xffaea42a, ["basalt_deltas"] = 0xffaea42a, ["crimson_forest"] = 0xffaea42a,
        ["soul_sand_valley"] = 0xffaea42a, ["warped_forest"] = 0xffaea42a, ["the_end"] = 0xff71a74d,
        ["end_barrens"] = 0xff71a74d, ["end_highlands"] = 0xff71a74d, ["end_midlands"] = 0xff71a74d,
        ["small_end_islands"] = 0xff71a74d, ["the_void"] = 0xff71a74d
    };

    internal static int? ReadBiomeTint(string id, string textureSuffix, JsonElement block)
    {
        if (!block.TryGetProperty("biome", out var biomeElement) || biomeElement.ValueKind != JsonValueKind.String)
            return null;
        var biome = biomeElement.GetString();
        if (string.IsNullOrWhiteSpace(biome)) return null;
        if (biome.StartsWith("minecraft:", StringComparison.Ordinal)) biome = biome[10..];
        if (biome.Length == 0) return null;

        uint rgb = VanillaBlockName(id) switch
        {
            "grass_block" when textureSuffix == "_snow" => 0,
            "grass_block" or "grass_block_side" or "grass_block_top"
                or "grass" or "short_grass" or "tall_grass" or "fern" or "large_fern" or "sugar_cane" or "bush"
                => GrassBiomeColors.GetValueOrDefault(biome, 0xff91bd59u),
            "birch_leaves" => 0xff80a755,
            "spruce_leaves" => 0xff619961,
            "oak_leaves" or "dark_oak_leaves" or "jungle_leaves" or "acacia_leaves" or "azalea_leaves"
                or "flowering_azalea_leaves" or "vine" or "lily_pad" or "small_dripleaf" or "big_dripleaf"
                or "big_dripleaf_stem" or "pitcher_crop" or "mangrove_propagule"
                => FoliageBiomeColors.GetValueOrDefault(biome, 0xff48b518u),
            _ => 0
        };
        return rgb == 0 ? null : unchecked((int)rgb);
    }

    internal static string VanillaBlockName(string id) => id.StartsWith("minecraft:", StringComparison.Ordinal) ? id[10..] : id;

    // Source strength is a property of the block state, not the light measured at its face.
    internal static (int Emission, int Filter) Read(string id, JsonElement block)
    {
        if (!id.StartsWith("minecraft:", StringComparison.Ordinal)) return default;
        var name = id[10..];
        var lit = HasState("lit", "true");
        var wet = HasState("waterlogged", "true");
        var emission = name switch
        {
            "torch" or "wall_torch" => TorchEmission,
            "soul_torch" or "soul_wall_torch" or "soul_lantern" or "soul_fire" => 10,
            "redstone_torch" or "redstone_wall_torch" => HasState("lit", "false") ? 0 : 7,
            "redstone_lamp" or "furnace" or "blast_furnace" or "smoker" or "campfire" or "soul_campfire" =>
                !lit || wet ? 0 : name switch
                {
                    "redstone_lamp" or "campfire" => 15,
                    "soul_campfire" => 10,
                    _ => 13
                },
            "glowstone" or "sea_lantern" or "lantern" or "shroomlight" or "jack_o_lantern" or "lava"
                or "fire" or "beacon" or "end_portal" or "ochre_froglight" or "verdant_froglight" or "pearlescent_froglight" => 15,
            "end_rod" => 14,
            "magma_block" or "brewing_stand" or "brown_mushroom" => 1,
            "light" => int.TryParse(State("level"), out var power) ? Math.Clamp(power, 0, 15) : 15,
            "candle" or "white_candle" or "orange_candle" or "magenta_candle" or "light_blue_candle" or "yellow_candle"
                or "lime_candle" or "pink_candle" or "gray_candle" or "light_gray_candle" or "cyan_candle" or "purple_candle"
                or "blue_candle" or "brown_candle" or "green_candle" or "red_candle" or "black_candle" =>
                HasState("lit", "true") && !wet ? 3 * (int.TryParse(State("candles"), out var count) ? Math.Clamp(count, 1, 4) : 1) : 0,
            _ => 0
        };
        return (emission, wet || name is "water" or "ice" || name.EndsWith("_leaves", StringComparison.Ordinal) ? 1 : 0);

        bool HasState(string key, string expected) => State(key) == expected;
        string? State(string key)
        {
            if (!block.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object
                || !props.TryGetProperty(key, out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
        }
    }
}
