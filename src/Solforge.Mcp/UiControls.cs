using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * One control Solforge drew in its last frame. Id is "surface:XXXXXXXX" (the window, "main" or "popoutN", and Dear
 * ImGui's id of the label), stable while the panel and label stay the same. Label is the visible text; Name is the
 * full ImGui label (with its "##" part), which tells apart controls that look alike. Rect is screen pixels [x, y, w, h].
 */
public sealed record UiControl(string Id, string Kind, string Label, string Name, string Window, string Scope,
    double[] Rect, bool Visible, string? Value, bool? Checked, IReadOnlyList<string>? Options);

/** What a ui_* call does to a control. */
public enum UiOperation { Invoke, SetValue, SetChecked, Select }

/** A resolved UI request: target control, operation and its value. */
public sealed record UiCommand(string ControlId, UiOperation Operation, string? Value, bool Checked);

/** Search, resolution and operation checks for the ui_* tools; pure, so the checks run without Solforge. */
public static class UiControls
{
    public static readonly string[] Kinds = ["button", "menu", "menu_item", "selectable", "checkbox", "radio", "tab", "text", "combo", "slider", "number", "header"];

    /** The operations each kind takes. */
    public static bool Allows(string kind, UiOperation operation) => operation switch
    {
        UiOperation.Invoke => kind is "button" or "menu" or "menu_item" or "selectable" or "checkbox" or "radio" or "tab" or "combo" or "header",
        UiOperation.SetValue => kind is "text" or "slider" or "number" or "combo",
        UiOperation.SetChecked => kind is "checkbox" or "menu_item" or "header",
        UiOperation.Select => kind is "combo" or "tab" or "selectable" or "radio" or "header",
        _ => false
    };

    /** Relevance of a control to a search query; 0 means no match. */
    public static int Score(UiControl control, string query)
    {
        string wanted = Normal(query);
        if (wanted.Length == 0) return 1;
        string label = Normal(control.Label);
        if (label == wanted) return 100;
        if (Normal(control.Name) == wanted) return 95;
        if (label.StartsWith(wanted, StringComparison.Ordinal)) return 80;
        string[] words = wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.All(label.Contains)) return 60;
        string everything = Normal($"{control.Label} {control.Name} {control.Scope} {control.Window} {control.Kind} {control.Value}");
        return words.All(everything.Contains) ? 40 : 0;
    }

    /** Matching controls, best first; an empty query keeps drawing order. */
    public static IReadOnlyList<UiControl> Find(IEnumerable<UiControl> controls, string? query, string? kind, string? window, bool includeHidden)
    {
        var rows = controls
            .Where(control => includeHidden || control.Visible)
            .Where(control => string.IsNullOrWhiteSpace(kind) || string.Equals(control.Kind, kind.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(control => string.IsNullOrWhiteSpace(window) || Normal(control.Window).Contains(Normal(window), StringComparison.Ordinal))
            .Select((control, order) => (Control: control, Order: order, Score: string.IsNullOrWhiteSpace(query) ? 1 : Score(control, query)))
            .Where(row => row.Score > 0);
        return rows.OrderByDescending(row => row.Score).ThenByDescending(row => row.Control.Visible).ThenBy(row => row.Order)
            .Select(row => row.Control).ToArray();
    }

    /**
     * The one control args name: "id" exactly, else the best "query" match (narrowed by "kind" and "window"). Several
     * equally good matches with different ids is an error that lists them, so a click never lands on a guess.
     */
    public static (UiControl? Control, string? Error) Resolve(IReadOnlyList<UiControl> controls, JsonObject args)
    {
        if (McpJson.String(args, "id") is { Length: > 0 } id)
            return controls.FirstOrDefault(control => string.Equals(control.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)) is { } exact
                ? (exact, null) : (null, $"No control with id {id} is drawn now; call list_ui_controls (ids change when a panel closes).");
        string? query = McpJson.String(args, "query");
        if (string.IsNullOrWhiteSpace(query)) return (null, "Give an id from list_ui_controls, or a query with the control's visible label.");
        var found = Find(controls, query, McpJson.String(args, "kind"), McpJson.String(args, "window"), false);
        if (found.Count == 0) found = Find(controls, query, McpJson.String(args, "kind"), McpJson.String(args, "window"), true);
        if (found.Count == 0) return (null, $"No drawn control matches '{query}'; open its panel, tab or menu first, then list_ui_controls.");
        int best = Score(found[0], query);
        var tied = found.Where(control => Score(control, query) == best).ToArray();
        if (tied.Length > 1)
            return (null, $"'{query}' matches {tied.Length} controls equally; pass one id: " +
                string.Join("; ", tied.Take(8).Select(control => $"{control.Id} {control.Kind} '{control.Label}' in {Where(control)}")));
        return (found[0], null);
    }

    /** Window and scope path of a control, or "the editor". */
    public static string Where(UiControl control) =>
        string.Join(" > ", new[] { control.Window, control.Scope }.Where(part => part.Length > 0)) is { Length: > 0 } place ? place : "the editor";

    /** JSON row describing one control. */
    public static JsonObject ToJson(UiControl control)
    {
        var row = new JsonObject
        {
            ["id"] = control.Id, ["kind"] = control.Kind, ["label"] = control.Label, ["window"] = control.Window,
            ["rect"] = new JsonArray(control.Rect.Select(value => (JsonNode)Math.Round(value)).ToArray()), ["visible"] = control.Visible
        };
        if (control.Name != control.Label) row["name"] = control.Name;
        if (control.Scope.Length > 0) row["scope"] = control.Scope;
        if (control.Value is not null) row["value"] = control.Value;
        if (control.Checked is { } check) row["checked"] = check;
        if (control.Options is { Count: > 0 } options) row["options"] = new JsonArray(options.Take(50).Select(option => (JsonNode)option).ToArray());
        return row;
    }

    /** What the approve prompt says, e.g. "Click button 'Build' in Inspector". */
    public static string Summary(UiControl control, UiOperation operation, string? value, bool check) => operation switch
    {
        UiOperation.Invoke => $"Click {control.Kind.Replace('_', ' ')} '{Display(control)}' in {Where(control)}",
        UiOperation.SetValue => $"Set {control.Kind} '{Display(control)}' in {Where(control)} to \"{Clip(value)}\"",
        UiOperation.SetChecked => $"Turn {(check ? "on" : "off")} '{Display(control)}' in {Where(control)}",
        _ => $"Choose \"{Clip(value)}\" in {control.Kind} '{Display(control)}' ({Where(control)})"
    };

    private static string Display(UiControl control) => control.Label.Length > 0 ? control.Label : control.Name;

    private static string Clip(string? text) => text is null ? "" : text.Length > 80 ? text[..80] + "..." : text;

    private static string Normal(string? text) =>
        string.Join(' ', (text ?? "").ToLowerInvariant().Replace("&", "", StringComparison.Ordinal)
            .Split([' ', '\t', '_', '-', '.', '/', ':'], StringSplitOptions.RemoveEmptyEntries));
}
