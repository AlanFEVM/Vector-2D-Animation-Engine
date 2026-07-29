using System.Reflection.Metadata;

[assembly: MetadataUpdateHandler(typeof(VectorAnimationEngine.HotReloadHandler))]

namespace VectorAnimationEngine;

internal static class HotReloadHandler
{
    private static readonly object Sync = new();
    private static HotReloadPlan? _cacheInvalidationPlan;

    public static void ClearCache(Type[]? updatedTypes)
    {
        if (updatedTypes is not { Length: > 0 }) return;
        var plan = HotReloadModuleResolver.Resolve(updatedTypes);
        lock (Sync)
        {
            _cacheInvalidationPlan = _cacheInvalidationPlan is { } pending ? pending.Merge(plan) : plan;
        }
    }

    public static void UpdateApplication(Type[]? updatedTypes)
    {
        HotReloadPlan plan;
        lock (Sync)
        {
            if (updatedTypes is { Length: > 0 })
            {
                var updatePlan = HotReloadModuleResolver.Resolve(updatedTypes);
                plan = _cacheInvalidationPlan is { } pending ? pending.Merge(updatePlan) : updatePlan;
            }
            else
            {
                plan = _cacheInvalidationPlan ?? HotReloadModuleResolver.Resolve(null);
            }
            _cacheInvalidationPlan = null;
        }

        AppHost.EnqueueHotReload(plan);
    }
}
