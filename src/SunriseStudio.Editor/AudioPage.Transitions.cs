using ImGuiNET;
using SunriseStudio.Audio;

namespace SunriseStudio.Editor;

/** AudioPage part: previews a transition between two segments or into a playlist and prepares it for playback. */
public sealed partial class AudioPage
{
    private uint transitionContainer, transitionSource, transitionDestination;
    private float transitionTime = 8;
    private int transitionVariation;
    private AudioAnalysis? transitionAnalysis;
    private IReadOnlyList<AudioSegment> transitionSegments = [];
    private bool transitionToPlaylist;
    private IReadOnlyList<AudioPlaylist> transitionPlaylists = [];

    /** Picks a transition container, source and destination, then renders the change between them. */
    private void DrawTransitions(AudioAnalysis analysis)
    {
        if (analysis.TransitionContainers.Count == 0 || !ImGui.TreeNode("Transition audition")) return;
        if (!analysis.TransitionContainers.ContainsKey(transitionContainer))
            transitionContainer = analysis.TransitionContainers.Values.OrderByDescending(value => value.Rules.Count).First().Id;
        ImGui.SetNextItemWidth(310);
        bool changed = false;
        if (UiAuto.BeginCombo("Container", analysis.TransitionContainers[transitionContainer].Name))
        {
            foreach (var container in analysis.TransitionContainers.Values)
                if (UiAuto.Selectable(container.Name + "##" + container.Id, container.Id == transitionContainer))
                { transitionContainer = container.Id; changed = true; }
            UiAuto.EndCombo();
        }
        if (changed || !ReferenceEquals(analysis, transitionAnalysis))
        {
            transitionAnalysis = analysis;
            transitionSegments = AudioTimeline.TransitionSegments(analysis, transitionContainer);
            transitionPlaylists = AudioTimeline.TransitionPlaylists(analysis, transitionContainer);
            transitionSource = transitionSegments.FirstOrDefault()?.Id ?? 0;
            transitionDestination = transitionSegments.LastOrDefault()?.Id ?? 0;
            transitionToPlaylist = false;
        }
        SegmentPicker("Source", ref transitionSource);
        ImGui.BeginDisabled(transitionPlaylists.Count == 0);
        if (UiAuto.Checkbox("Playlist destination", ref transitionToPlaylist))
            transitionDestination = transitionToPlaylist ? transitionPlaylists.FirstOrDefault()?.Id ?? 0 : transitionSegments.FirstOrDefault()?.Id ?? 0;
        ImGui.EndDisabled();
        if (transitionToPlaylist)
        {
            ImGui.SetNextItemWidth(310);
            if (UiAuto.BeginCombo("Destination", analysis.Playlists.GetValueOrDefault(transitionDestination)?.Name ?? "Choose playlist"))
            {
                foreach (var playlist in transitionPlaylists)
                    if (UiAuto.Selectable(playlist.Name + "##" + playlist.Id, playlist.Id == transitionDestination)) transitionDestination = playlist.Id;
                UiAuto.EndCombo();
            }
        }
        else SegmentPicker("Destination", ref transitionDestination);
        double exit = analysis.Segments.TryGetValue(transitionSource, out var source) ? (source.Exit - source.Entry) / 1000 : 0;
        ImGui.SetNextItemWidth(150); UiAuto.InputFloat("Change after (s)", ref transitionTime, 1, 8, "%.3f");
        transitionTime = Math.Clamp(float.IsFinite(transitionTime) ? transitionTime : 0, 0, (float)exit);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("From the entry cue; the rule picks the sync point.");
        if (source is not null)
        {
            try
            {
                var (_, meter) = AudioSynchronization.Meter(analysis, source.Id);
                ImGui.TextDisabled($"{meter.Tempo:0.###} BPM  |  {meter.BeatsPerBar}/{meter.BeatValue}");
            }
            catch (NotSupportedException) { }
            if (source.Cues is { Count: > 2 } && ImGui.TreeNode("Source cues"))
            {
                foreach (var cue in source.Cues.Skip(1).SkipLast(1))
                    ImGui.TextUnformatted($"{(cue.PositionMs - source.Entry) / 1000:0.000} s  |  {cue.Name ?? (cue.Id == 0 ? "Custom cue" : $"Cue {cue.Id}")}");
                ImGui.TreePop();
            }
        }
        if (session?.Timeline is { Synchronization: { } point } timeline && timeline.Playlist == transitionContainer)
            ImGui.TextDisabled($"Prepared: {AudioSynchronization.Label(point.Kind)}  |  {point.SecondsFromEntry:0.000} s");
        if (analysis.TransitionContainers[transitionContainer].Rules.Any(rule => rule.DestinationEntry is 2 or 3))
        {
            ImGui.SetNextItemWidth(150); UiAuto.InputInt("Destination variation", ref transitionVariation);
            transitionVariation = Math.Max(0, transitionVariation);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Repeatable random pick.");
        }
        if (session?.Timeline is { Destination: { } entry } prepared && prepared.Playlist == transitionContainer)
            ImGui.TextDisabled($"{AudioDestination.Label(entry.Kind)}  |  {entry.SecondsFromEntry:0.000} s into destination");
        ImGui.BeginDisabled(transitionSource == 0 && transitionDestination == 0);
        if (UiAuto.Button("Prepare transition")) PrepareTransition(false);
        ImGui.SameLine(); if (UiAuto.Button("Play transition")) PrepareTransition(true);
        ImGui.EndDisabled();
        ImGui.TreePop();
    }

    private void SegmentPicker(string label, ref uint selected)
    {
        ImGui.SetNextItemWidth(310);
        uint current = selected;
        if (!UiAuto.BeginCombo(label, selected == 0 ? "Nothing" : transitionSegments.FirstOrDefault(value => value.Id == current)?.Name ?? "Choose segment")) return;
        if (UiAuto.Selectable("Nothing", selected == 0)) selected = 0;
        foreach (var segment in transitionSegments)
            if (UiAuto.Selectable(segment.Name + "##" + segment.Id, segment.Id == selected)) selected = segment.Id;
        UiAuto.EndCombo();
    }

    private void PrepareTransition(bool play)
    {
        var active = session!;
        uint container = transitionContainer, source = transitionSource, destination = transitionDestination;
        double time = transitionTime; int variation = transitionVariation, loops = previewLoops, branch = timelineVariation;
        bool playlist = transitionToPlaylist;
        player.Stop(); loadedWave = ""; info = null; selectedMedia = selectedComposition = -1; unsupported = "";
        Begin("Rendering music transition", async token =>
        {
            var rendered = playlist ? await active.RenderPlaylistTransition(container, source, destination, time, loops, branch, token, variation)
                : await active.RenderTransition(container, source, destination, time, token, variation);
            return new(Wave: rendered.Path, Info: PcmWave.ReadInfo(rendered.Path) with { Encoding = "Music transition (dry)" }, Play: play,
                Pcm: PcmWave.Read(rendered.Path));
        });
    }
}
