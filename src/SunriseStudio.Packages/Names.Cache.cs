namespace SunriseStudio.Packages;

/** Cache half of LocalizedStrings: serialises its maps. */
public sealed partial class LocalizedStrings
{
    /** Writes the string maps to the names cache. */
    internal void WriteCache(NameCacheWriter writer)
    {
        writer.Map(unique, writer.Text);
        writer.Map(ambiguous, values => writer.Array(values, writer.Text));
        writer.Map(byContainer, values => writer.Map(values, writer.Text));
        writer.Map(englishData, writer.Data.Write);
        writer.Map(bankErrors, writer.Text);
        writer.Data.Write(FailedContainerCount);
        writer.Array(Diagnostics, writer.Text);
    }

    /** Reads the string maps back from the names cache. */
    internal static LocalizedStrings ReadCache(NameCacheReader reader)
    {
        var result = new LocalizedStrings();
        reader.Into(result.unique, reader.Text);
        reader.Into(result.ambiguous, () => reader.Array(reader.Text));
        reader.Into(result.byContainer, () => reader.Map(reader.Text));
        reader.Into(result.englishData, reader.Data.ReadUInt32);
        reader.Into(result.bankErrors, reader.Text);
        result.FailedContainerCount = reader.Count();
        result.Diagnostics = reader.Array(reader.Text);
        return result;
    }
}

/** Cache half of ComponentIndex: serialises its lookup maps and facts. */
public sealed partial class ComponentIndex
{
    /** Writes every lookup map, the config facts, the entity links and the slot catalog. */
    internal void WriteCache(NameCacheWriter writer)
    {
        writer.Map(primaries, writer.Data.Write); writer.Map(prompts, writer.Text);
        writer.Map(entityKinds, writer.Text); writer.Map(referenceLabels, writer.Text);
        writer.Map(soundIds, writer.Data.Write); writer.Map(entitySounds, writer.Data.Write);
        writer.Map(patterns, writer.Text); writer.Map(combatants, writer.Text);
        writer.Map(objectTypes, writer.Data.Write); writer.Map(nameHashes, writer.Data.Write);
        writer.Map(modelTags, writer.Data.Write);
        writer.Map(configs, row =>
        {
            writer.Data.Write(row.Primary); writer.Optional(row.Prompt);
            writer.Data.Write(row.Particles); writer.Data.Write(row.Audio);
            writer.Optional(row.Combatant); writer.Optional(row.Pattern);
            writer.Data.Write(row.Sound.HasValue); if (row.Sound.HasValue) writer.Data.Write(row.Sound.Value);
            writer.Array(row.Classes, writer.Data.Write); writer.Array(row.Dependencies, writer.Data.Write);
            writer.Array(row.StringHashes, writer.Data.Write);
        });
        writer.Map(entityConfigs, rows => writer.Array(rows, writer.Data.Write));
        slots.WriteCache(writer);
    }

    /** Restores what WriteCache wrote and rebuilds the dependency sets. */
    internal static ComponentIndex ReadCache(NameCacheReader reader)
    {
        var result = new ComponentIndex { building = new() };
        reader.Into(result.primaries, reader.Data.ReadUInt32); reader.Into(result.prompts, reader.Text);
        reader.Into(result.entityKinds, reader.Text); reader.Into(result.referenceLabels, reader.Text);
        reader.Into(result.soundIds, reader.Data.ReadUInt32); reader.Into(result.entitySounds, reader.Data.ReadUInt32);
        reader.Into(result.patterns, reader.Text); reader.Into(result.combatants, reader.Text);
        reader.Into(result.objectTypes, reader.Data.ReadByte); reader.Into(result.nameHashes, reader.Data.ReadUInt32);
        reader.Into(result.modelTags, reader.Data.ReadUInt32);
        var configs = reader.Map(() => new ConfigFacts(reader.Data.ReadUInt32(), reader.Optional(),
            reader.Data.ReadBoolean(), reader.Data.ReadBoolean(), reader.Optional(), reader.Optional(),
            reader.Data.ReadBoolean() ? reader.Data.ReadUInt32() : null,
            reader.Array(reader.Data.ReadUInt32), reader.Array(reader.Data.ReadUInt32), reader.Array(reader.Data.ReadUInt32)));
        foreach (var pair in configs) result.SetConfig(pair.Key, pair.Value);
        foreach (var pair in reader.Map(() => reader.Array(reader.Data.ReadUInt32)))
        {
            result.building.Entities.Add(pair.Key, pair.Value);
            AddLinks(result.building.Dependencies, pair.Key, pair.Value);
        }
        foreach (var pair in result.nameHashes)
            AddLinks(result.building.Strings, pair.Key, [pair.Value]);
        result.slots = ComponentSlotCatalog.ReadCache(reader);
        result.FinishBuild();
        return result;
    }
}

/** Cache half of GameNames: serialises its name maps. */
public sealed partial class GameNames
{
    private GameNames(PackageCatalog catalog, Func<uint, string?>? fallback, LocalizedStrings strings,
        IReadOnlyList<string> diagnostics)
    { this.catalog = catalog; hashFallback = fallback; Strings = strings; Diagnostics = diagnostics; }

    /** Every lookup map is restored, including component categories and scoped localization. */
    internal void WriteCache(NameCacheWriter writer)
    {
        Strings.WriteCache(writer);
        writer.Map(scenarios, scenario =>
        {
            writer.Data.Write(scenario.Tag); writer.Optional(scenario.DevName);
            writer.Data.Write(scenario.StringsTag); writer.Data.Write(scenario.ActivityHash);
            writer.Optional(scenario.Activity); writer.Data.Write(scenario.DestinationHash);
            writer.Optional(scenario.Destination);
        });
        writer.Map(mapDevNames, writer.Text); writer.Map(mapTitles, writer.Text);
        writer.Data.Write(stringsByDevName.Count);
        foreach (var pair in stringsByDevName)
        { writer.Text(pair.Key); writer.Array(pair.Value, writer.Data.Write); }
        writer.Map(tagTitles, writer.Text); writer.Map(bubbleParents, writer.Data.Write);
        writer.Array(Diagnostics, writer.Text);
        (Components ?? throw new InvalidOperationException("Incomplete component index cannot be cached.")).WriteCache(writer);
    }

    /** Restores what WriteCache wrote, in the same order. */
    internal static GameNames ReadCache(NameCacheReader reader, PackageCatalog catalog,
        Func<uint, string?>? fallback)
    {
        var strings = LocalizedStrings.ReadCache(reader);
        var scenarios = reader.Map(() => new ScenarioNames(reader.Data.ReadUInt32(), reader.Optional(),
            reader.Data.ReadUInt32(), reader.Data.ReadUInt32(), reader.Optional(),
            reader.Data.ReadUInt32(), reader.Optional()));
        var dev = reader.Map(reader.Text); var titles = reader.Map(reader.Text);
        var scoped = new Dictionary<string, uint[]>(StringComparer.Ordinal);
        int count = reader.Count();
        for (int i = 0; i < count; i++) scoped.Add(reader.Text(), reader.Array(reader.Data.ReadUInt32));
        var tags = reader.Map(reader.Text); var bubbles = reader.Map(reader.Data.ReadUInt32);
        var diagnostics = reader.Array(reader.Text);
        var result = new GameNames(catalog, fallback, strings, diagnostics);
        foreach (var pair in scenarios) result.scenarios.Add(pair.Key, pair.Value);
        foreach (var pair in dev) result.mapDevNames.Add(pair.Key, pair.Value);
        foreach (var pair in titles) result.mapTitles.Add(pair.Key, pair.Value);
        foreach (var pair in scoped) result.stringsByDevName.Add(pair.Key, pair.Value);
        foreach (var pair in tags) result.tagTitles.Add(pair.Key, pair.Value);
        foreach (var pair in bubbles) result.bubbleParents.Add(pair.Key, pair.Value);
        result.components = ComponentIndex.ReadCache(reader);
        return result;
    }
}
