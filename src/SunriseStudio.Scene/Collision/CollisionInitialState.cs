using SunriseStudio.Packages;

namespace SunriseStudio.Scene.Collision;

/** Which collision parts start enabled for an entity, or why that could not be resolved. */
public sealed record CollisionInitialState(bool[]? EnabledParts, string? Unresolved)
{
    /** Whether the part starts enabled; null when unknown. */
    public bool? Enabled(int part) => EnabledParts is { } parts && (uint)part < parts.Length ? parts[part] : null;
}

/** Collision keeps the shared component-state evaluator's unresolved result. */
public sealed class CollisionInitialStateReader(PackageCatalog catalog)
{
    public ISet<uint>? Reads { get; init; }

    /**
     * @param previewVariant Variant row name requested as a spawn would; zero requests none.
     * @param picks Variant parameter values the user picked; they apply last.
     */
    public CollisionInitialState Read(uint entityTag, uint physicsConfig, uint spawnTag = 0, int spawnOffset = -1,
        uint previewVariant = 0, IReadOnlyDictionary<uint, uint>? picks = null)
    {
        var result = new EntityInitialStateReader(catalog) { Reads = Reads }
            .Read(entityTag, physicsConfig, spawnTag, spawnOffset, previewVariant, picks: picks);
        return new(result.EnabledParts, result.Unresolved);
    }
}
