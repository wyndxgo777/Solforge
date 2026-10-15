using System.Globalization;

namespace Solforge.Weapons;

/** One problem with a recipe: the snake_case path of the field and Parhelion's wording of the rule. */
public sealed record WeaponRecipeIssue(string Field, string Message)
{
    public override string ToString() => $"{Field}: {Message}";
}

/** The rules a recipe must meet before it can build, rewritten from Parhelion's weapon/validation.rs.
 *  Parhelion stops at the first failure; this collects every issue so an editor can list them. Rules that
 *  need the installed game (socket plug sets, stat bounds) run in ValidateAgainstDonor. */
public static class WeaponRecipeValidator
{
    public const int MaxPatchBytes = 4096, MaxProgramInstructions = 256, MaxTextCharacters = ushort.MaxValue;

    public static void ThrowIfInvalid(WeaponRecipe recipe)
    {
        var issues = Validate(recipe);
        if (issues.Count > 0) throw new WeaponRecipeException(string.Join(Environment.NewLine, issues));
    }

    public static IReadOnlyList<WeaponRecipeIssue> Validate(WeaponRecipe recipe)
    {
        var issues = new List<WeaponRecipeIssue>();
        void Add(string field, string message) => issues.Add(new(field, message));

        if (recipe.Schema != WeaponRecipe.CurrentSchema)
            Add("schema", $"Unsupported recipe schema {recipe.Schema}; expected {WeaponRecipe.CurrentSchema}");
        if (WeaponNamespace.Problem(recipe.Namespace) is { } namespaceProblem) Add("namespace", namespaceProblem);
        DonorHash(recipe.Donor, "donor", "The donor", Add);
        if (recipe.PresentationDonor is { } presentation) DonorHash(presentation, "presentation_donor", "The geometry donor", Add);
        Identity(recipe, Add);
        Text(recipe, Add);
        Stats(recipe.Overrides, Add);
        Scalars(recipe.Overrides, Add);
        for (int lane = 0; lane < recipe.Overrides.SocketColumns.Count; lane++)
        {
            if (recipe.Overrides.SocketColumns[lane] is { } column) SocketColumn(lane, column, Add);
        }
        RawPatches(recipe.Overrides.RawPayloadPatches, Add);
        return issues;
    }

    /** Checks a recipe against the stock weapon it names and the installed catalog: socket lanes, socket types and
     *  plug sets, weapon-pattern and stat-group indices, stat definitions and bounds. */
    public static IReadOnlyList<WeaponRecipeIssue> ValidateAgainstDonor(WeaponRecipe recipe, WeaponDonorProfile donor)
    {
        var issues = new List<WeaponRecipeIssue>();
        void Add(string field, string message) => issues.Add(new(field, message));

        if (!recipe.Donor.ItemHash.TryParse(out uint donorHash) || donorHash != donor.ItemHash)
            Add("donor.item_hash", $"Recipe donor {recipe.Donor.ItemHash} is not {donor.Name} ({HexHash.From(donor.ItemHash)})");
        if (recipe.Donor.ExpectedName is { } expected && !string.Equals(expected, donor.Name, StringComparison.Ordinal))
            Add("donor.expected_name", $"The donor is now named \"{donor.Name}\", not \"{expected}\"");

        var columns = recipe.Overrides.SocketColumns;
        for (int lane = 0; lane < columns.Count; lane++)
        {
            if (columns[lane] is not { } column) continue;
            string field = $"overrides.socket_columns[{lane}]";
            if (lane >= donor.Sockets.Count)
            {
                Add(field, $"Authored socket column {lane} is outside the donor's {donor.Sockets.Count} sockets");
                continue;
            }
            if (column.SocketType is { } socketType && socketType != ushort.MaxValue
                && donor.KnownSocketTypes is { } socketTypes && !socketTypes.Contains(socketType))
                Add(field, $"Socket {lane} uses unknown native socket type {socketType}");
            foreach (var (kind, index) in new[] { ("reusable", column.ReusablePlugSetIndex), ("randomized", column.RandomizedPlugSetIndex) })
            {
                if (index is { } plugSet && donor.PlugSetCount is { } plugSets && plugSet < ushort.MaxValue && plugSet >= plugSets)
                    Add(field, $"Socket column {lane} selects {kind} plug-set index {plugSet}, but the installed table contains {plugSets} rows");
            }
            var socket = donor.Sockets[lane];
            if (column.Choices.Count > socket.MaxAuthoredChoices)
            {
                Add(field, socket.MaxAuthoredChoices == 0
                    ? $"Authored socket column {lane} adds plugs to a socket the donor disables"
                    : $"Authored socket column {lane} has {column.Choices.Count} choices; the donor socket takes at most {socket.MaxAuthoredChoices}");
            }
            if (socket.SupportedPlugs is not { } supported) continue;
            foreach (var choice in column.Choices)
            {
                if (choice.TryParse(out uint plug) && !supported.Contains(plug))
                    Add(field, $"Authored socket column {lane} plug {choice} is not in the donor socket's plug set");
            }
        }

        if (recipe.Overrides.WeaponPatternIndex is { } pattern && donor.WeaponPatternIndices is { } patterns && !patterns.Contains(pattern))
            Add("overrides.weapon_pattern_index", $"Weapon-pattern index {pattern} is not used by any installed stock weapon");
        if (recipe.Overrides.StatGroupIndex is { } group && donor.StatGroupIndices is { } groups && !groups.Contains(group))
            Add("overrides.stat_group_index", $"Stat-display group {group} is not used by any installed stock weapon");

        var known = donor.Stats.Concat(donor.AddableStats).GroupBy(stat => stat.DefinitionIndex)
            .ToDictionary(group => group.Key, group => group.First());
        var stats = recipe.Overrides.InvestmentStats;
        for (int i = 0; i < stats.Count; i++)
        {
            string field = $"overrides.investment_stats[{i}]";
            if (!known.TryGetValue(stats[i].DefinitionIndex, out var stat))
            {
                Add(field, $"Investment stat definition {stats[i].DefinitionIndex} is neither on the donor nor an installed stat");
                continue;
            }
            if (stats[i].Value < stat.Minimum || stats[i].Value > stat.Maximum)
                Add(field, $"{stat.Name} value {stats[i].Value} is outside its stat-group range {stat.Minimum}..{stat.Maximum}");
        }
        foreach (ushort removed in recipe.Overrides.RemovedInvestmentStats)
        {
            if (!donor.Stats.Any(stat => stat.DefinitionIndex == removed))
                Add("overrides.removed_investment_stats", $"Investment stat definition {removed} is not on the donor, so it cannot be removed");
        }
        return issues;
    }

    private static void DonorHash(WeaponDonorReference donor, string field, string label, Action<string, string> add)
    {
        if (!donor.ItemHash.TryParse(out uint hash))
            add($"{field}.item_hash", $"{field}.item_hash must be a canonical hash of the form 0xXXXXXXXX, not {donor.ItemHash}");
        else if (hash is 0 or WeaponRecipe.ReservedEmptyNameHash)
            add($"{field}.item_hash", $"{label} item hash must be nonzero and cannot use the reserved empty-name hash");
    }

    /** Parhelion's WeaponCloneIdentity::validate_for_donor over all twelve hashes, omitted optional ones derived
     *  from the namespace first: nonzero, not the empty-name hash, pairwise distinct, item hash not the donor's,
     *  and every localized hash above the donor string bank's terminal hash. */
    private static void Identity(WeaponRecipe recipe, Action<string, string> add)
    {
        var seen = new Dictionary<uint, string>();
        foreach (var (role, field, hash, derived) in recipe.Identity.Resolve(recipe.Namespace))
        {
            string label = derived ? $"{field} (derived from the namespace)" : field;
            if (!hash.TryParse(out uint value))
            {
                add(field, $"{field} must be a canonical hash of the form 0xXXXXXXXX, not {hash}");
                continue;
            }
            if (value is 0 or WeaponRecipe.ReservedEmptyNameHash)
            {
                add(field, $"{label} must be nonzero and cannot use the reserved empty-name hash");
                continue;
            }
            if (seen.TryGetValue(value, out string? other)) add(field, $"{label} repeats {other}; weapon identity hashes must be pairwise distinct");
            else seen[value] = field;
            if (role == 0 && recipe.Donor.ItemHash.TryParse(out uint donor) && value == donor)
                add(field, "The authored item hash collides with its donor");
            if (role >= WeaponIdentityHashes.FirstLocalizedRole && value <= WeaponIdentityHashes.DonorStringBankTerminal)
                add(field, $"{label} 0x{value:X8}: Localized hashes must sort after the donor bank's terminal hash (0x{WeaponIdentityHashes.DonorStringBankTerminal:X8})");
        }
    }

    private static void Text(WeaponRecipe recipe, Action<string, string> add)
    {
        LocalizedText("name", "Weapon name", recipe.Name, add);
        foreach (var (field, label, value) in OptionalText(recipe.TypeName, recipe.CollectionName, recipe.CollectionDescription,
            recipe.InventoryHint, recipe.CollectionRequirement))
        {
            if (value is not null) LocalizedText(field, label, value, add);
        }
        if (recipe.Flavor.Length > 0) LocalizedText("flavor", "Flavor text", recipe.Flavor, add);
        LocalizedText("source", "Source text", recipe.Source, add);

        var locales = new HashSet<byte>();
        for (int i = 0; i < recipe.LocaleOverrides.Count; i++)
        {
            var locale = recipe.LocaleOverrides[i];
            string field = $"locale_overrides[{i}]";
            if (locale.LocaleIndex >= WeaponRecipe.LocaleCount || !locales.Add(locale.LocaleIndex))
                add(field, $"Localized payload override {locale.LocaleIndex} is outside 0..{WeaponRecipe.LocaleCount - 1} or duplicated");
            var primary = OptionalText(recipe.TypeName, recipe.CollectionName, recipe.CollectionDescription,
                recipe.InventoryHint, recipe.CollectionRequirement).ToList();
            var localized = OptionalText(locale.TypeName, locale.CollectionName, locale.CollectionDescription,
                locale.InventoryHint, locale.CollectionRequirement).ToList();
            for (int k = 0; k < primary.Count; k++)
            {
                if (localized[k].Value is null) continue;
                if (primary[k].Value is null)
                    add($"{field}.{localized[k].Field}", $"Locale {locale.LocaleIndex} cannot replace {primary[k].Label.ToLowerInvariant()} because the primary recipe does not author that optional text field");
                else LocalizedText($"{field}.{localized[k].Field}", primary[k].Label, localized[k].Value!, add);
            }
            if (locale.Name is { } name) LocalizedText($"{field}.name", "Weapon name", name, add);
            if (locale.Flavor is { Length: > 0 } flavor) LocalizedText($"{field}.flavor", "Flavor text", flavor, add);
            if (locale.Source is { } source) LocalizedText($"{field}.source", "Source text", source, add);
        }
    }

    private static IEnumerable<(string Field, string Label, string? Value)> OptionalText(string? typeName,
        string? collectionName, string? collectionDescription, string? inventoryHint, string? collectionRequirement)
    {
        yield return ("type_name", "Item type", typeName);
        yield return ("collection_name", "Collection name", collectionName);
        yield return ("collection_description", "Collection description", collectionDescription);
        yield return ("inventory_hint", "Inventory hint", inventoryHint);
        yield return ("collection_requirement", "Collection requirement", collectionRequirement);
    }

    private static void LocalizedText(string field, string label, string value, Action<string, string> add)
    {
        if (string.IsNullOrWhiteSpace(value)) add(field, $"{label} cannot be empty");
        else if (value.Contains('\0')) add(field, $"{label} cannot contain a NUL character");
        else if (value.EnumerateRunes().Count() > MaxTextCharacters) add(field, $"{label} exceeds the localized 16-bit character limit");
    }

    private static void Stats(WeaponRecipeOverrides overrides, Action<string, string> add)
    {
        var overridden = new HashSet<ushort>();
        foreach (var stat in overrides.InvestmentStats)
        {
            if (!overridden.Add(stat.DefinitionIndex))
                add("overrides.investment_stats", $"Investment stat definition {stat.DefinitionIndex} is overridden more than once");
        }
        var removed = new HashSet<ushort>();
        foreach (ushort definition in overrides.RemovedInvestmentStats)
        {
            if (definition > byte.MaxValue)
                add("overrides.removed_investment_stats", $"Removed investment stat definition {definition} does not fit the native 8-bit field");
            if (!removed.Add(definition))
                add("overrides.removed_investment_stats", $"Investment stat definition {definition} is removed more than once");
            if (overridden.Contains(definition))
                add("overrides.removed_investment_stats", $"Investment stat definition {definition} cannot be both overridden and removed");
        }
    }

    private static void Scalars(WeaponRecipeOverrides overrides, Action<string, string> add)
    {
        if (overrides.MaxStackSize is { } stack && (stack == 0 || stack > int.MaxValue))
            add("overrides.max_stack_size", "Weapon max stack size must fit the native positive signed 32-bit field");
        if (overrides.PowerCapGroup is not null && overrides.PowerCapGroups is not null)
            add("overrides.power_cap_groups", "Choose either one power-cap group for every version row or advanced per-row power-cap groups, not both");
        else if (overrides.PowerCapGroups is { Count: 0 })
            add("overrides.power_cap_groups", "Advanced power-cap groups must contain at least one table index");
        if (overrides.WeaponPatternIndex == ushort.MaxValue)
            add("overrides.weapon_pattern_index", "Weapon pattern index 65535 is a native disabled sentinel");
        if (overrides.Tuning is { } tuning)
        {
            if (tuning.RoundsPerMinute is <= 0) add("overrides.tuning.rounds_per_minute", "Rounds per minute must be positive");
            if (tuning.Magazine is <= 0) add("overrides.tuning.magazine", "Magazine size must be positive");
            if (tuning.DamagePerShot is { } damage && (!float.IsFinite(damage) || damage <= 0))
                add("overrides.tuning.damage_per_shot", "Damage per shot must be finite and positive");
        }
    }

    private static void SocketColumn(int lane, WeaponSocketColumnRecipe column, Action<string, string> add)
    {
        string field = $"overrides.socket_columns[{lane}]";
        if (column.SocketType == ushort.MaxValue && column.Choices.Count == 0)
        {
            if (column.ChoiceWeightBits.Count > 0 || column.ChoiceConditions.Count > 0 || column.ReusablePlugSetIndex is not null
                || column.RandomizedPlugSetIndex is not null || column.RandomizedSelectionProgram.Count > 0)
                add(field, $"Removed socket {lane} cannot retain plug selection settings");
            return;
        }
        if (column.Choices.Count is < 1 or > WeaponRecipe.MaxSocketChoices)
            add(field, $"Authored socket column {lane} must contain between one and {WeaponRecipe.MaxSocketChoices} choices");
        var plugs = new HashSet<uint>();
        for (int i = 0; i < column.Choices.Count; i++)
        {
            if (!column.Choices[i].TryParse(out uint hash))
                add($"{field}.choices[{i}]", $"{field}.choices[{i}] must be a canonical hash of the form 0xXXXXXXXX, not {column.Choices[i]}");
            else if (hash == 0) add(field, $"Authored socket column {lane} contains plug hash zero");
            else if (!plugs.Add(hash)) add(field, $"Authored socket column {lane} contains duplicate plug 0x{hash:X8}");
        }
        if (column.SocketType == ushort.MaxValue)
            add(field, $"Authored socket column {lane} uses the disabled socket-type sentinel");
        if (column.ChoiceWeightBits.Count > 0)
        {
            if (column.ChoiceWeightBits.Count != column.Choices.Count)
                add(field, $"Authored socket column {lane} has {column.ChoiceWeightBits.Count} weights for {column.Choices.Count} choices");
            var weights = column.ChoiceWeightBits.Select(BitConverter.UInt32BitsToSingle).ToList();
            if (weights.Any(weight => !float.IsFinite(weight) || weight < 0) || !weights.Any(weight => weight > 0))
                add(field, $"Authored socket column {lane} weights must be finite, nonnegative, and include at least one positive value");
        }
        if (column.ChoiceConditions.Count > 0)
        {
            if (column.ChoiceConditions.Count != column.Choices.Count)
                add(field, $"Authored socket column {lane} has {column.ChoiceConditions.Count} conditions for {column.Choices.Count} choices");
            for (int i = 0; i < column.ChoiceConditions.Count; i++)
                Program($"{field}.choice_conditions[{i}]", $"Socket column {lane} choice {i} condition", column.ChoiceConditions[i], true, add);
        }
        if (column.ReusablePlugSetIndex == ushort.MaxValue || column.RandomizedPlugSetIndex == ushort.MaxValue)
            add(field, $"Authored socket column {lane} uses 65535 as an active plug-set index");
        if (column.RandomizedSelectionProgram.Count > 0 && column.RandomizedPlugSetIndex is null)
            add(field, $"Authored socket column {lane} has a randomized selection program without a randomized plug set");
        Program($"{field}.randomized_selection_program", $"Socket column {lane} randomized selection program",
            column.RandomizedSelectionProgram, column.RandomizedPlugSetIndex is null, add);
    }

    /** Length rules for an RPN program. Stack-depth checking needs the opcode table and lands with Phase 21. */
    private static void Program(string field, string label, IReadOnlyList<WeaponNumericInstruction> program, bool allowEmpty,
        Action<string, string> add)
    {
        if (program.Count == 0 && !allowEmpty) add(field, $"{label} cannot be empty");
        else if (program.Count > MaxProgramInstructions) add(field, $"{label} cannot contain more than {MaxProgramInstructions} instructions");
    }

    private static void RawPatches(IReadOnlyList<WeaponRawPayloadPatchRecipe> patches, Action<string, string> add)
    {
        if (patches.Any(patch => patch.Target == RecipeRawPayloadTarget.ItemDefinition)
            && patches.Any(patch => patch.Target.IsItemDefinitionSubBlock()))
            add("overrides.raw_payload_patches", "Raw gameplay-definition patches cannot be mixed with named item-block patches; choose one coordinate space so final bytes are unambiguous");
        var ranges = new List<(RecipeRawPayloadTarget Target, ulong Start, ulong End, int Index)>();
        for (int i = 0; i < patches.Count; i++)
        {
            string field = $"overrides.raw_payload_patches[{i}]";
            if (!TryParsePatchBytes(patches[i].Bytes, out byte[] bytes))
            {
                add(field, $"Raw payload patch {i} bytes must contain a non-empty even number of hexadecimal digits");
                continue;
            }
            if (bytes.Length > MaxPatchBytes)
            {
                add(field, $"Raw payload patch {i} exceeds the {MaxPatchBytes}-byte per-patch limit");
                continue;
            }
            ulong start = patches[i].Offset, end = start + (ulong)bytes.Length;
            if (end > uint.MaxValue)
            {
                add(field, $"Raw payload patch {i} range overflows");
                continue;
            }
            foreach (var other in ranges)
            {
                if (other.Target == patches[i].Target && start < other.End && other.Start < end)
                    add(field, $"Raw payload patches {other.Index} and {i} overlap");
            }
            ranges.Add((patches[i].Target, start, end, i));
        }
    }

    /** Parhelion's parse_raw_patch_bytes: ASCII whitespace and '_' are ignored, then an optional 0x prefix. */
    public static bool TryParsePatchBytes(string? text, out byte[] bytes)
    {
        bytes = [];
        if (text is null) return false;
        string compact = string.Concat(text.Where(c => c is not ('_' or ' ' or '\t' or '\n' or '\r' or '\f')));
        if (compact.StartsWith("0x", StringComparison.Ordinal) || compact.StartsWith("0X", StringComparison.Ordinal)) compact = compact[2..];
        if (compact.Length == 0 || compact.Length % 2 != 0 || !compact.All(char.IsAsciiHexDigit)) return false;
        bytes = new byte[compact.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(compact.AsSpan(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return true;
    }
}
