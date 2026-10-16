using System.Security.Cryptography;
using ImGuiNET;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

/** Each asset retains its draft; saving recompiles against the bytes that draft opened. */
public sealed class PerkBehaviorPage : IStudioPage
{
    private sealed class Draft
    {
        public PerkBehaviorEditor Editor { get; } = new();
        public string SourceHash = "";
    }

    private readonly Dictionary<string, Draft> drafts = [];
    // Document bytes are replaced, never edited in place, so the reference keys the hash.
    private byte[]? hashedBytes;
    private string bytesHash = "";
    public string Id => "sunrise.perk-behavior";
    public string Title => "Perk behavior";
    public bool CanOpen(StudioDocument document) => document.Entry.Reference == InvestmentSandboxPerkAuthoring.GameplayClass
        && document.Entry.Type is 8 or 16;

    /** Saving refuses a source changed by another edit while this draft was open. */
    public void Draw(StudioPageContext context)
    {
        var workspace = context.Workspace;
        if (workspace.Document is not { } document || !CanOpen(document)) return;
        string key = (workspace.Project?.ManifestPath ?? "view") + ":" + document.Entry.TagHash;
        if (!ReferenceEquals(hashedBytes, document.Bytes)) { hashedBytes = document.Bytes; bytesHash = Hash(document.Bytes); }
        string hash = bytesHash;
        if (!drafts.TryGetValue(key, out var draft))
        {
            draft = new();
            draft.Editor.Load(Open(document.Bytes, document.Entry.TagHash));
            draft.SourceHash = hash;
            drafts.Add(key, draft);
        }
        else if (draft.SourceHash != hash && !draft.Editor.IsDirty && !workspace.IsBusy)
        {
            draft.Editor.Load(Open(document.Bytes, document.Entry.TagHash));
            draft.SourceHash = hash;
        }
        bool changed = draft.SourceHash != hash;
        bool dirty = draft.Editor.IsDirty;
        var problems = draft.Editor.Problems;
        ImGui.BeginDisabled(!workspace.CanEdit || !dirty || changed || problems.Count != 0);
        if (UiAuto.Button("Compile and save")) workspace.Run(() =>
        {
            if (workspace.Document?.Entry.TagHash != document.Entry.TagHash
                || Hash(workspace.Document.Bytes) != draft.SourceHash)
                throw new InvalidDataException("The perk changed. Reload its source before saving.");
            byte[] compiled = draft.Editor.Compile();
            workspace.ApplyBytes(compiled, "Saved perk behavior");
            draft.Editor.Load(Open(compiled, document.Entry.TagHash));
            draft.SourceHash = Hash(compiled);
        });
        ImGui.EndDisabled();
        StudioUi.Tip(StudioUi.MayNotWork + (workspace.IsProjectTag(document.Entry.TagHash) ? "" : " Changes every item with this perk."));
        ImGui.SameLine();
        ImGui.BeginDisabled(workspace.IsBusy);
        if (UiAuto.Button("Reload source")) workspace.Run(() =>
        {
            draft.Editor.Load(Open(document.Bytes, document.Entry.TagHash));
            draft.SourceHash = hash;
        });
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Discard this draft and decompile the current project bytes.");
        ImGui.EndDisabled(); ImGui.SameLine();
        ImGui.TextDisabled(dirty ? "Unsaved changes" : "Saved");
        if (changed) ImGui.TextWrapped("Source changed. Reload before saving.");
        if (workspace.Error.Length != 0) ImGui.TextWrapped(workspace.Error);
        draft.Editor.Draw("package-perk", workspace.CanEdit && !changed);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /** Damage-resist effects store their program's tag, so the graph carries it. */
    private static PerkBehaviorGraph Open(byte[] bytes, uint tag)
    {
        var graph = NativePerkBehavior.Decompile(bytes);
        graph.ProgramTag = tag;
        return graph;
    }
}
