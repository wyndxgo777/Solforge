using System.Numerics;
using ImGuiNET;
using SunriseStudio.Audio;

namespace SunriseStudio.Editor;

/** AudioPage part: draws the volume and parameter automation envelopes of a prepared timeline plan. */
public sealed partial class AudioPage
{
    private sealed record EnvelopePlot(string Name, double Offset, double Duration, float[] Samples);
    private AudioTimelinePlan? plottedTimeline;
    private IReadOnlyList<EnvelopePlot> envelopePlots = [];

    /** Plot samples are rebuilt only when a prepared timeline changes. */
    private void DrawAutomation(AudioTimelinePlan plan)
    {
        if (!ReferenceEquals(plottedTimeline, plan))
        {
            var plots = new List<EnvelopePlot>();
            for (int i = 0; i < plan.Clips.Count; i++)
            {
                var clip = plan.Clips[i];
                foreach (var automation in clip.Automation ?? [])
                {
                    var curve = new AudioAutomationCurve(automation);
                    float[] samples = new float[129];
                    for (int n = 0; n < samples.Length; n++)
                        samples[n] = curve.GainAt((float)Math.Round((clip.AutomationOffsetSeconds + clip.DurationSeconds * n / (samples.Length - 1)) * 48000,
                            MidpointRounding.AwayFromZero));
                    string role = automation.Type switch { 0 => "Volume", 3 => "Fade in", 4 => "Fade out", _ => "Envelope" };
                    plots.Add(new($"Clip {i + 1} / {role}", clip.StartSeconds, clip.DurationSeconds, samples));
                }
            }
            plottedTimeline = plan; envelopePlots = plots;
        }
        if (envelopePlots.Count == 0 || !UiAuto.TreeNode($"Clip envelopes ({envelopePlots.Count})###clip-envelopes")) return;
        ImGui.BeginChild("audio-envelopes", new(0, 230), ImGuiChildFlags.Borders);
        int index = 0;
        foreach (var plot in envelopePlots)
        {
            ImGui.TextUnformatted($"{plot.Name}  {Time(plot.Offset)} - {Time(plot.Offset + plot.Duration)}");
            Vector2 size = new(Math.Max(80, ImGui.GetContentRegionAvail().X), 64);
            ImGui.InvisibleButton($"envelope-{index++}", size);
            Vector2 start = ImGui.GetItemRectMin(); var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(start, start + size, ImGui.GetColorU32(ImGuiCol.FrameBg));
            float minimum = Math.Min(0, plot.Samples.Min()), maximum = Math.Max(1, plot.Samples.Max());
            Vector2 Point(int sample) => start + new Vector2(size.X * sample / (plot.Samples.Length - 1),
                size.Y - 3 - (size.Y - 6) * (plot.Samples[sample] - minimum) / (maximum - minimum));
            for (int n = 1; n < plot.Samples.Length; n++) draw.AddLine(Point(n - 1), Point(n), ImGui.GetColorU32(ImGuiCol.PlotLines), 1.5f);
            if (ImGui.IsItemHovered())
            {
                int at = Math.Clamp((int)((ImGui.GetMousePos().X - start.X) / size.X * (plot.Samples.Length - 1)), 0, plot.Samples.Length - 1);
                ImGui.SetTooltip($"Gain {plot.Samples[at]:F3} / {plot.Duration * at / (plot.Samples.Length - 1):F3} s");
            }
        }
        ImGui.EndChild(); ImGui.TreePop();
    }
}
