using ImGuiNET;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/** StudioForm part: particle effect channel and sequence controls shared by both viewports. */
public sealed partial class StudioForm
{
    // Both viewports share one set so a choice made in the inspector holds in either view.
    private readonly EffectControls effectControls = new();

    /**
     * Channel values, sequence plays and force-on toggles for one placement's particle effects.
     * @param selected The selected effect marker, or null to show every effect of the scene.
     */
    private void DrawEffectControls(SceneDocument scene, SceneMarker? selected)
    {
        var key = selected is null ? (EffectPlacementKey?)null : EffectPlacementKey.Of(selected);
        var markers = scene.Markers.Where(marker => marker.Effect is { Kind: EffectKind.ParticleSystem }
            && (key is null || EffectPlacementKey.Of(marker) == key)).ToArray();
        if (markers.Length == 0 || !UiAuto.CollapsingHeader("Effects##effect-controls", ImGuiTreeNodeFlags.DefaultOpen)) return;
        bool showAll = effectControls.ShowAll;
        if (UiAuto.Checkbox("Show all effects", ref showAll)) effectControls.ShowAll = showAll;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Run every particle effect all the time, whatever starts it in the game.");
        foreach (var placement in markers.GroupBy(EffectPlacementKey.Of))
        {
            ImGui.PushID($"{placement.Key.SourceTag:X8}:{placement.Key.SourceOffset}:{placement.Key.Identity:X16}");
            DrawEffectChannels(placement.Key, placement);
            DrawEffectSequences(placement.Key, placement);
            ImGui.PopID();
        }
        if (selected?.Effect is { } effect)
        {
            bool forced = effectControls.Forced(selected);
            if (UiAuto.Checkbox("Force on", ref forced)) effectControls.SetForced(selected, forced);
            ImGui.SameLine();
            ImGui.TextDisabled(effect.Activation?.Trigger() ?? "sequence not read");
        }
        else if (UiAuto.TreeNode($"Force on ({markers.Length})##effect-force"))
        {
            for (int index = 0; index < markers.Length; index++)
            {
                bool forced = effectControls.Forced(markers[index]);
                if (UiAuto.Checkbox($"{markers[index].Name}##force{index}", ref forced)) effectControls.SetForced(markers[index], forced);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(markers[index].Effect!.Activation?.Trigger() ?? "sequence not read");
            }
            ImGui.TreePop();
        }
    }

    /** One row per gate channel: a checkbox sets 0 or 1, the field takes any value. */
    private void DrawEffectChannels(EffectPlacementKey key, IEnumerable<SceneMarker> markers)
    {
        var gates = markers.SelectMany(marker => marker.Effect!.Activation?.Gates ?? []).GroupBy(gate => gate.Channel).ToArray();
        if (gates.Length == 0) return;
        ImGui.TextDisabled("Channels");
        StudioUi.Tip("Values the game sets on the object. 0 here until set.");
        for (int index = 0; index < gates.Length; index++)
        {
            var channel = gates[index];
            string? resolved = workspace.ResolveHash(channel.Key);
            string name = resolved ?? $"Channel {index + 1}";
            float value = effectControls.Channel(key, channel.Key);
            bool on = value != 0;
            if (UiAuto.Checkbox($"##on{channel.Key:X8}", ref on)) effectControls.SetChannel(key, channel.Key, on ? 1 : 0);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70 * scale);
            if (UiAuto.DragFloat($"{name}##value{channel.Key:X8}", ref value, 0.01f))
                effectControls.SetChannel(key, channel.Key, value);
            StudioUi.Tip((resolved is null ? $"0x{channel.Key:X8}\n" : "")
                + string.Join("\n", channel.Select(gate => $"Open from {gate.Min:G4} up to {gate.Max:G4}").Distinct()));
        }
    }

    /** Sequences that gameplay starts, or that hold play-once nodes, get Play and Stop. */
    private void DrawEffectSequences(EffectPlacementKey key, IEnumerable<SceneMarker> markers)
    {
        var sequences = markers.Select(marker => marker.Effect!.Activation).OfType<EffectActivation>()
            .GroupBy(activation => activation.SequenceTag)
            .Where(group => group.Any(activation => !activation.AutoStart || !activation.Continuous)).ToArray();
        foreach (var sequence in sequences)
        {
            bool autoStart = sequence.First().AutoStart;
            var (playing, generation) = effectControls.Sequence(key, sequence.Key, autoStart);
            ImGui.PushID($"sequence{sequence.Key:X8}");
            if (UiAuto.SmallButton("Play")) effectControls.Play(key, sequence.Key, autoStart);
            ImGui.SameLine();
            ImGui.BeginDisabled(!playing);
            if (UiAuto.SmallButton("Stop")) effectControls.Stop(key, sequence.Key, autoStart);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextUnformatted(workspace.DisplayName(sequence.Key));
            StudioUi.Tip($"{(autoStart ? "Starts on spawn" : "Started by gameplay")}\nPlayed {generation} times\n{sequence.Key:X8}");
            ImGui.PopID();
        }
    }
}
