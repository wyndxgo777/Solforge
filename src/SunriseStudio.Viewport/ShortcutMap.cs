using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SunriseStudio.Viewport;

/** The chosen preset plus per-action overrides. The window thread owns it; it needs no ImGui. */
public sealed class ShortcutMap
{
    public const int FormatVersion = 1;
    // Separates bindings in text; the same word is shown in menus and the Shortcuts window.
    private const string Separator = " or ";
    private readonly Dictionary<string, IReadOnlyList<ShortcutBinding>> overrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> texts = new(StringComparer.Ordinal);
    private string[]? boundKeys;

    public ShortcutPreset Preset { get; private set; } = ShortcutPresets.Default;
    /** Changes whenever any binding changes. */
    public int Revision { get; private set; }
    public bool IsCustom => overrides.Count > 0;
    public string Title => IsCustom ? $"Custom (from {Preset.Name})" : Preset.Name;
    public IReadOnlyCollection<string> OverriddenIds => overrides.Keys;

    /** Replaces every binding; an unknown name selects the Sunrise Studio preset. */
    public void UsePreset(string name)
    {
        Preset = ShortcutPresets.Find(name) ?? ShortcutPresets.Default;
        overrides.Clear();
        Changed();
    }

    /** Active bindings of an action: the user's override, else the preset's. */
    public IReadOnlyList<ShortcutBinding> Bindings(string id) =>
        overrides.TryGetValue(id, out var own) ? own : Preset.For(id);

    /** True when the user changed the action from the preset. */
    public bool IsOverridden(string id) => overrides.ContainsKey(id);

    /**
     * Replaces one slot, or adds the binding when the slot is empty.
     * @return False for an unknown action, a slot outside 0-1, or a kind the action refuses.
     */
    public bool Set(string id, int slot, ShortcutBinding binding)
    {
        if (ShortcutActions.Find(id) is not { } action || !action.Allows(binding.Kind)
            || slot < 0 || slot >= ShortcutPresets.MaximumBindings) return false;
        var next = Bindings(id).ToList();
        if (slot < next.Count) next[slot] = binding;
        else next.Add(binding);
        Store(id, next);
        return true;
    }

    /** Removes one binding slot; ignores an unknown action or empty slot. */
    public void Clear(string id, int slot)
    {
        var next = Bindings(id).ToList();
        if (ShortcutActions.Find(id) is null || slot < 0 || slot >= next.Count) return;
        next.RemoveAt(slot);
        Store(id, next);
    }

    /** Returns the action to its preset bindings. */
    public void Reset(string id)
    {
        if (overrides.Remove(id)) Changed();
    }

    /** Display text of an action's bindings, cached. */
    public string Text(string id)
    {
        if (!texts.TryGetValue(id, out string? text)) texts[id] = text = string.Join(Separator, Bindings(id));
        return text;
    }

    /** Key names used by any key binding, so a frame polls only those keys. */
    public IReadOnlyList<string> BoundKeys => boundKeys ??= ShortcutActions.All.SelectMany(action => Bindings(action.Id))
        .Where(binding => binding.Kind == ShortcutKind.Key).Select(binding => binding.Key).Distinct(StringComparer.Ordinal).ToArray();

    /** Menu shortcuts are global, so they collide with 2D and 3D; 2D and 3D never collide. */
    public static bool Shares(ShortcutSection first, ShortcutSection second) =>
        first == second || first == ShortcutSection.Menus || second == ShortcutSection.Menus;

    /** Equal modifier counts can tie when both sets are held; a click and drag share a button. */
    public static bool Collides(ShortcutBinding first, ShortcutBinding second) =>
        BitOperations.PopCount((uint)first.Modifiers) == BitOperations.PopCount((uint)second.Modifiers)
        && SameInput(first, second);

    private static bool SameInput(ShortcutBinding first, ShortcutBinding second) => (first.Kind, second.Kind) switch
        {
            (ShortcutKind.Key, ShortcutKind.Key) => first.Key == second.Key,
            (ShortcutKind.Wheel, ShortcutKind.Wheel) => true,
            (ShortcutKind.Click or ShortcutKind.Drag, ShortcutKind.Click or ShortcutKind.Drag) => first.Button == second.Button,
            _ => false
        };

    /** Other actions that bind the same input where both can apply. */
    public IReadOnlyList<ShortcutAction> ConflictsWith(string id, ShortcutBinding binding)
    {
        if (ShortcutActions.Find(id) is not { } owner) return [];
        return ShortcutActions.All.Where(other => other.Id != id && Shares(owner.Section, other.Section)
            && Bindings(other.Id).Any(candidate => Competes(owner, other, binding, candidate))).ToArray();
    }

    /**
     * A more-specific match at the minimum shared chord also wins at every larger chord.
     * @param owner Action whose binding is inspected.
     * @param other Potential competing action.
     * @param first Owner binding.
     * @param second Competing binding.
     * @return True when both can have the highest rank in their shared scope.
     */
    private bool Competes(ShortcutAction owner, ShortcutAction other, ShortcutBinding first, ShortcutBinding second)
    {
        if (!Collides(first, second)) return false;
        var view = owner.Section == ShortcutSection.Menus ? other.Section : owner.Section;
        var held = first.Modifiers | second.Modifiers;
        int rank = BitOperations.PopCount((uint)first.Modifiers);
        return !ShortcutActions.All.Where(action => action.Section == ShortcutSection.Menus || action.Section == view)
            .SelectMany(action => Bindings(action.Id)).Any(binding => SameInput(first, binding)
                && (binding.Modifiers & ~held) == 0 && BitOperations.PopCount((uint)binding.Modifiers) > rank);
    }

    /** Number of bindings that collide with another action's binding. */
    public int ConflictCount() => ShortcutActions.All.Sum(action =>
        Bindings(action.Id).Count(binding => ConflictsWith(action.Id, binding).Count > 0));

    /**
     * Key actions a press fires: the most specific bindings among menus and the view under the pointer.
     * @param held Modifiers held now; a binding matches when it needs no modifier outside them.
     * @param view The view under the pointer, or null when none is.
     */
    public IReadOnlyList<string> ResolveKey(string key, ShortcutModifiers held, ShortcutSection? view) =>
        Best(action => action.Section == ShortcutSection.Menus || action.Section == view,
            binding => binding.Kind == ShortcutKind.Key && binding.Key == key, held);

    /** Click and drag actions a press of this button starts in the view, most specific only. */
    public IReadOnlyList<string> ResolveMouse(ShortcutSection view, ShortcutButton button, ShortcutModifiers held) =>
        Best(action => action.Section == view,
            binding => binding.Kind is ShortcutKind.Click or ShortcutKind.Drag && binding.Button == button, held);

    /** Menu actions a click of this button fires, wherever the pointer is. */
    public IReadOnlyList<string> ResolveMenuClick(ShortcutButton button, ShortcutModifiers held) =>
        Best(action => action.Section == ShortcutSection.Menus,
            binding => binding.Kind == ShortcutKind.Click && binding.Button == button, held);

    /** Actions of a section that a wheel event with the held modifiers should trigger. */
    public IReadOnlyList<string> ResolveWheel(ShortcutSection view, ShortcutModifiers held) =>
        Best(action => action.Section == view, binding => binding.Kind == ShortcutKind.Wheel, held);

    /** More modifiers win; competing equally specific actions fire nothing. */
    private List<string> Best(Func<ShortcutAction, bool> scope, Func<ShortcutBinding, bool> input, ShortcutModifiers held)
    {
        List<string> result = [];
        int best = -1;
        foreach (var action in ShortcutActions.All)
        {
            if (!scope(action)) continue;
            foreach (var binding in Bindings(action.Id))
            {
                if (!input(binding) || (binding.Modifiers & ~held) != 0) continue;
                int rank = BitOperations.PopCount((uint)binding.Modifiers);
                if (rank < best) continue;
                if (rank > best) { best = rank; result.Clear(); }
                if (!result.Contains(action.Id)) result.Add(action.Id);
            }
        }
        return result.Count == 1 ? result : [];
    }

    /**
     * Reads bindings joined by " or ". An empty text is an explicit unbound entry.
     * @return False when any part is malformed, refused by the action, or over the limit.
     */
    public static bool TryParseList(string? text, ShortcutAction action, out IReadOnlyList<ShortcutBinding> bindings)
    {
        bindings = [];
        if (string.IsNullOrWhiteSpace(text)) return true;
        List<ShortcutBinding> parsed = [];
        foreach (string part in text.Split(Separator))
        {
            if (!ShortcutBinding.TryParse(part, out var binding) || !action.Allows(binding.Kind)) return false;
            if (!parsed.Contains(binding)) parsed.Add(binding);
        }
        if (parsed.Count > ShortcutPresets.MaximumBindings) return false;
        bindings = parsed;
        return true;
    }

    /** The stored form: format version, preset name, and overrides as text lists. */
    public JsonElement ToElement()
    {
        using var document = JsonDocument.Parse(ToJson());
        return document.RootElement.Clone();
    }

    /** Serialises the user's overrides for preferences. */
    public string ToJson()
    {
        var stored = new JsonObject();
        foreach (var (id, bindings) in overrides.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            stored[id] = new JsonArray(bindings.Select(binding => (JsonNode?)JsonValue.Create(binding.ToString())).ToArray());
        var root = new JsonObject
        {
            ["Version"] = FormatVersion,
            ["Preset"] = Preset.Name,
            ["Overrides"] = stored
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /** Damaged text gives the Sunrise Studio preset. */
    public static ShortcutMap FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            using var document = JsonDocument.Parse(json);
            return FromElement(document.RootElement);
        }
        catch (JsonException) { return new(); }
    }

    /**
     * Reads the stored form without throwing.
     * @return Another version, or a missing section, gives the Sunrise Studio preset. An unknown
     *     action or a malformed binding keeps that action's preset bindings.
     */
    public static ShortcutMap FromElement(JsonElement? section)
    {
        var map = new ShortcutMap();
        if (section is not { ValueKind: JsonValueKind.Object } root) return map;
        if (!root.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int number) || number != FormatVersion) return map;
        if (root.TryGetProperty("Preset", out var preset) && preset.ValueKind == JsonValueKind.String
            && ShortcutPresets.Find(preset.GetString()) is { } found) map.Preset = found;
        if (!root.TryGetProperty("Overrides", out var stored) || stored.ValueKind != JsonValueKind.Object) return map;
        foreach (var property in stored.EnumerateObject())
        {
            if (ShortcutActions.Find(property.Name) is not { } action || property.Value.ValueKind != JsonValueKind.Array
                || property.Value.GetArrayLength() > ShortcutPresets.MaximumBindings) continue;
            List<ShortcutBinding> bindings = [];
            bool valid = true;
            foreach (var item in property.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !ShortcutBinding.TryParse(item.GetString(), out var binding)
                    || !action.Allows(binding.Kind)) { valid = false; break; }
                bindings.Add(binding);
            }
            if (valid) map.Store(action.Id, bindings);
        }
        return map;
    }

    /** An override equal to the preset is dropped, so the title stops saying Custom. */
    private void Store(string id, IEnumerable<ShortcutBinding> bindings)
    {
        var next = bindings.Distinct().Take(ShortcutPresets.MaximumBindings).ToArray();
        if (next.SequenceEqual(Preset.For(id))) overrides.Remove(id);
        else overrides[id] = next;
        Changed();
    }

    private void Changed()
    {
        Revision++;
        texts.Clear();
        boundKeys = null;
    }
}
