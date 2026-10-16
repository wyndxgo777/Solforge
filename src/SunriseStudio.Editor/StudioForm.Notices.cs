using System.Text;
using System.Text.RegularExpressions;
using ImGuiNET;

namespace SunriseStudio.Editor;

public sealed partial class StudioForm
{
    private static readonly (string Title, string Resource, bool Markdown)[] NoticeTabs =
    [
        ("Studio", "SunriseStudio.Notices.Studio", true),
        (".NET runtime", "SunriseStudio.Notices.DotNet", false),
        ("Community Toolkit", "SunriseStudio.Notices.Toolkit", false),
        ("Chakra Petch", "SunriseStudio.Notices.ChakraPetch", false),
        ("Rajdhani", "SunriseStudio.Notices.Rajdhani", false)
    ];
    private bool noticesDialog;
    private NoticeBlock[][]? notices;

    /** The license notices ship inside the executable, so a release is the one file. */
    private void DrawNoticesDialog()
    {
        ImGui.SetNextWindowPos(new(ClientSize.Width / 2f, ClientSize.Height / 2f), ImGuiCond.Appearing, new(.5f));
        ImGui.SetNextWindowSize(new(Math.Min(860 * scale, ClientSize.Width - 40), Math.Min(640 * scale, ClientSize.Height - 40)),
            ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("Third-party notices", ImGuiWindowFlags.NoSavedSettings)) return;
        notices ??= NoticeTabs.Select(tab => NoticeBlock.Parse(ReadNotice(tab.Resource), tab.Markdown))
            .ToArray();
        if (ImGui.BeginTabBar("notice-tabs"))
        {
            for (int i = 0; i < NoticeTabs.Length; i++)
                if (UiAuto.BeginTabItem(NoticeTabs[i].Title))
                {
                    if (ImGui.BeginChild("notice-text", new(0, -ImGui.GetFrameHeightWithSpacing()), ImGuiChildFlags.Borders,
                        ImGuiWindowFlags.HorizontalScrollbar))
                        DrawNotice(notices[i]);
                    ImGui.EndChild();
                    UiAuto.EndTabItem();
                }
            ImGui.EndTabBar();
        }
        if (UiAuto.Button("Close", new(100 * scale, 0))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawNotice(NoticeBlock[] blocks)
    {
        for (int i = 0; i < blocks.Length; i++)
        {
            var block = blocks[i];
            switch (block.Kind)
            {
                case NoticeKind.Title:
                    ImGui.PushFont(gui!.TitleFont); ImGui.TextUnformatted(block.Text); ImGui.PopFont();
                    break;
                case NoticeKind.Heading:
                    ImGui.Spacing(); ImGui.TextColored(StudioTheme.Accent, block.Text);
                    break;
                case NoticeKind.Code:
                    ImGui.PushFont(gui!.MonospaceFont); ImGui.TextUnformatted(block.Text); ImGui.PopFont();
                    break;
                case NoticeKind.Table:
                    DrawNoticeTable(i, block.Rows!);
                    break;
                default:
                    ImGui.TextWrapped(block.Text);
                    break;
            }
            ImGui.Spacing();
        }
    }

    private static void DrawNoticeTable(int id, string[][] rows)
    {
        if (!ImGui.BeginTable($"notice-table-{id}", rows[0].Length,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;
        foreach (string heading in rows[0]) ImGui.TableSetupColumn(heading);
        ImGui.TableHeadersRow();
        foreach (var row in rows.Skip(1))
        {
            ImGui.TableNextRow();
            for (int column = 0; column < rows[0].Length; column++)
            {
                ImGui.TableSetColumnIndex(column);
                ImGui.TextWrapped(column < row.Length ? row[column] : "");
            }
        }
        ImGui.EndTable();
    }

    private static string ReadNotice(string resource)
    {
        using var stream = typeof(StudioForm).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException("A license notice is missing from Studio.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder(reader.ReadToEnd());
        // The default font covers Latin-1 only.
        text.Replace('—', '-').Replace('–', '-').Replace('‘', '\'').Replace('’', '\'')
            .Replace('“', '"').Replace('”', '"').Replace('ł', 'l').Replace("\r\n", "\n");
        for (int i = 0; i < text.Length; i++) if (text[i] > 'ÿ') text[i] = '?';
        return text.ToString();
    }

    private enum NoticeKind { Title, Heading, Paragraph, Code, Table }

    private sealed record NoticeBlock(NoticeKind Kind, string Text, string[][]? Rows = null)
    {
        /** Splits the notices markdown into display blocks; a plain text file is one code block. */
        public static NoticeBlock[] Parse(string text, bool markdown)
        {
            if (!markdown) return [new(NoticeKind.Code, text)];
            var blocks = new List<NoticeBlock>();
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd();
                if (line.Length == 0) continue;
                if (line.StartsWith("```"))
                {
                    int end = i + 1;
                    while (end < lines.Length && !lines[end].StartsWith("```")) end++;
                    blocks.Add(new(NoticeKind.Code, string.Join('\n', lines[(i + 1)..end])));
                    i = end;
                }
                else if (line.StartsWith('#'))
                    blocks.Add(new(line.StartsWith("# ") ? NoticeKind.Title : NoticeKind.Heading, Inline(line.TrimStart('#').Trim())));
                else if (line.StartsWith('|'))
                {
                    var rows = new List<string[]>();
                    for (; i < lines.Length && lines[i].StartsWith('|'); i++)
                        if (!lines[i].StartsWith("|---"))
                            rows.Add(lines[i].Trim().Trim('|').Split('|').Select(cell => Inline(cell.Trim())).ToArray());
                    i--;
                    blocks.Add(new(NoticeKind.Table, "", rows.ToArray()));
                }
                else
                {
                    // A paragraph runs to the next blank line.
                    var paragraph = new List<string>();
                    for (; i < lines.Length && lines[i].Trim().Length != 0
                        && (paragraph.Count == 0 || !lines[i].StartsWith('#') && !lines[i].StartsWith('|') && !lines[i].StartsWith("```")); i++)
                        paragraph.Add(lines[i].Trim());
                    i--;
                    blocks.Add(new(NoticeKind.Paragraph, Inline(string.Join(' ', paragraph))));
                }
            }
            return blocks.ToArray();
        }

        private static string Inline(string text) =>
            Regex.Replace(Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", "$1 ($2)"), @"<(https?://[^>]+)>", "$1")
                .Replace("`", "").Replace("**", "");
    }
}
