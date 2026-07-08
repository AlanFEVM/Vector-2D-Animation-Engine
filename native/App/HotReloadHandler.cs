using System.Reflection.Metadata;

[assembly: MetadataUpdateHandler(typeof(VectorAnimationEngine.HotReloadHandler))]

namespace VectorAnimationEngine;

internal static class HotReloadHandler
{
    public static void ClearCache(Type[]? updatedTypes)
    {
    }

    public static void UpdateApplication(Type[]? updatedTypes)
    {
        AppHost.ReloadMainFormForHotReload();
    }
}
