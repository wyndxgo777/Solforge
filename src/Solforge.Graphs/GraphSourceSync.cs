/**
 * File: two-way sync between a graph's nodes and its source text, shared by the unified Graph panel's models
 * (a mission script's Lua and a node graph's .solgraph.json). UI-free so the rules are tested without ImGui.
 */
using System.Text.Json;

namespace Solforge.Graphs;

/**
 * Holds the source view's text for one open graph. Model edits (nodes, undo, reload) replace the text unless the user
 * has an unapplied draft; text edits apply to the model after a short pause (Live) or on Apply. A draft that does
 * not parse is never applied and never overwritten: its error stays in Error (the Checks tab) until it is fixed,
 * applied or reverted.
 */
public sealed class GraphSourceSync
{
    private double editedAt = double.NegativeInfinity;
    // The draft text that last failed to parse; it is not retried until it changes.
    private string? failed;

    /** The text shown in the source view. */
    public string Draft { get; private set; } = "";
    /** The model's text as of the last sync or apply. */
    public string Applied { get; private set; } = "";
    /** Why the draft did not apply; empty when it did or nothing was tried. */
    public string Error { get; private set; } = "";
    /** True when the model changed while a draft was pending, so the draft no longer starts from the current graph. */
    public bool Stale { get; private set; }
    /** Apply drafts after Delay seconds without typing; false waits for Apply. */
    public bool Live { get; set; } = true;
    public double Delay { get; set; } = .75;
    public bool HasDraft => Draft != Applied;

    /** A new document (open, close, discard): drops any draft and error. */
    public void Reset(string text)
    {
        Draft = Applied = text;
        Error = "";
        Stale = false;
        failed = null;
    }

    /** The model's current text; called each frame or after each model edit. A pending draft is kept, not clobbered. */
    public void Sync(string modelText)
    {
        if (modelText == Applied) return;
        bool pending = HasDraft;
        Applied = modelText;
        if (!pending || Draft == modelText) { Draft = modelText; Error = ""; Stale = false; failed = null; }
        else Stale = true;
    }

    /** The user typed in the source view at time now (seconds). */
    public void Edit(string text, double now)
    {
        if (text == Draft) return;
        Draft = text;
        editedAt = now;
        if (!HasDraft) { Error = ""; Stale = false; failed = null; }
    }

    /** True when a live apply should run now: a pending draft, Live on, and Delay seconds since the last keystroke. */
    public bool Due(double now) => Live && HasDraft && failed != Draft && now - editedAt >= Delay;

    /**
     * Parses and commits the draft through apply (which must throw before changing the model when the text is bad).
     * @param force Apply now (the Apply button, Save, or leaving the editor) instead of waiting for Due.
     * @return True when the model took the draft.
     */
    public bool TryApply(Action<string> apply, double now, bool force = false)
    {
        if (!HasDraft || !force && !Due(now)) return false;
        string text = Draft;
        try { apply(text); }
        catch (Exception error) when (Parsing(error))
        {
            Error = error.Message;
            failed = text;
            return false;
        }
        Applied = text;
        Error = "";
        Stale = false;
        failed = null;
        return true;
    }

    /** Drops the draft and takes the model's text again. */
    public void Revert() => Reset(Applied);

    /** Exceptions a bad source text raises; anything else is a real fault and propagates. */
    public static bool Parsing(Exception error) => error is InvalidDataException or JsonException or FormatException
        or ArgumentException or InvalidOperationException or NotSupportedException;
}

/** Which graph model the unified Graph panel shows; the strings are the MCP graph_kind values. */
public static class GraphModels
{
    public const string MissionScript = "mission_script";
    public const string NodeGraphPrefix = "node_graph:";

    /** graph_kind for a node graph document kind, e.g. node_graph:weapon. */
    public static string NodeGraph(GraphKind kind) => NodeGraphPrefix + kind switch
    {
        GraphKind.Material => "material", GraphKind.Weapon => "weapon", GraphKind.Mission => "mission", _ => "unknown"
    };

    /** The model a file opens in: .lua is a mission script, *.solgraph.json a node graph; anything else none (null). */
    public static string? ForPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) return MissionScript;
        if (path.EndsWith(GraphDocument.Extension, StringComparison.OrdinalIgnoreCase)) return NodeGraphPrefix + "unknown";
        return null;
    }

    /** The source view's language for a node graph: its own JSON, with the compiled Lua or ops beside it. */
    public static string SourceLabel(GraphKind kind) => kind == GraphKind.Mission ? "Lua" : "Source";

    /** Parses node graph source text; throws InvalidDataException or JsonException and changes nothing on bad text. */
    public static GraphDocument ParseNodeSource(string text, GraphKind expected)
    {
        var document = GraphDocument.FromJson(text);
        if (document.Kind != expected)
            throw new InvalidDataException($"The source changes the graph kind from {expected} to {document.Kind}; create a new graph instead.");
        return document;
    }

    /** The compiled text a node graph produces: Lua for mission graphs, the compile JSON (ops, writes) otherwise. */
    public static string Compiled(GraphDocument document)
    {
        if (document.Kind == GraphKind.Mission) return MissionGraphCompiler.Compile(document).Lua;
        return GraphCommands.Compile(document).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
