using System.Globalization;
using ImGuiNET;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

/** ComponentsPage part: reads and edits the damage parts and health pools of a combatant entity. */
public sealed partial class ComponentsPage
{
    private const string AssumedHealth = "Meaning assumed. Read only.";
    private readonly Dictionary<string, float> damageDrafts = [];
    private EntityComponentsState? damageSource;
    private IReadOnlyList<CombatantDamage> damageConfigs = [];
    private int damageUnread;

    /** Damage parts and pools of the entity's damage configs; every edit is untested in game. */
    partial void DrawDamage(StudioPageContext context)
    {
        var workspace = context.Workspace;
        if (workspace.LoadedComponents is not { } state) return;
        // Parsed once per components read, from the bytes that read already holds.
        if (!ReferenceEquals(state, damageSource)) ReadDamage(state);
        if (damageConfigs.Count == 0 && damageUnread == 0) return;
        StudioUi.Heading("Damage", SharedEdit);
        if (damageUnread > 0)
        {
            ImGui.TextDisabled($"{damageUnread} damage config(s) not shown");
            StudioUi.Tip("Their layout does not match the known one, so they are not read.");
        }
        foreach (var damage in damageConfigs)
        {
            ImGui.PushID((int)damage.ConfigTag);
            if (damageConfigs.Count > 1) ImGui.TextDisabled($"Config {damage.ConfigTag:X8}");
            DrawDamageParts(workspace, damage);
            DrawDamagePools(workspace, damage);
            ImGui.PopID();
        }
    }

    private void ReadDamage(EntityComponentsState state)
    {
        var configs = new List<CombatantDamage>();
        int unread = 0;
        foreach (var section in state.Sections)
        {
            if (section.Bytes.Length == 0) continue;
            if (CombatantDamage.Read(section.Config, section.Bytes) is { } damage) configs.Add(damage);
            else if (CombatantDamage.IsDamageConfig(section.Bytes)) unread++;
        }
        damageSource = state;
        damageConfigs = configs;
        damageUnread = unread;
    }

    private void DrawDamageParts(StudioWorkspace workspace, CombatantDamage damage)
    {
        if (!ImGui.BeginTable("damage-parts", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("Part");
        ImGui.TableSetupColumn("Kind");
        ImGui.TableSetupColumn("Pool");
        ImGui.TableSetupColumn("Health?");
        ImGui.TableSetupColumn("Scale");
        ImGui.TableSetupColumn("Per type");
        ImGui.TableHeadersRow();
        foreach (var part in damage.Parts)
        {
            ImGui.PushID(part.Index);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(part.Name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(part.Kind.ToString(CultureInfo.InvariantCulture));
            StudioUi.Tip("Raw part kind. Meaning not known.");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(part.Pool.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn(); ImGui.TextDisabled(part.Health.ToString("G6", CultureInfo.InvariantCulture));
            StudioUi.Tip(AssumedHealth);
            ImGui.TableNextColumn();
            if (DamageInput(workspace, $"{damage.ConfigTag}:p{part.Index}", part.Scale, -1, StudioUi.MayNotWork, out float scale))
            {
                int index = part.Index;
                workspace.Run(() => workspace.ApplyDamageEdit(damage.ConfigTag,
                    (tag, bytes) => CombatantDamage.SetPartScale(tag, bytes, index, scale), "Changed damage scale"));
            }
            ImGui.TableNextColumn();
            if (part.TypeScales.Count == 0) ImGui.TextDisabled("-");
            foreach (var row in part.TypeScales)
            {
                if (row.Index > 0) ImGui.SameLine();
                if (!DamageInput(workspace, $"{damage.ConfigTag}:p{part.Index}:t{row.Index}", row.Scale,
                    ImGui.GetFontSize() * 4, $"Type row {row.Index}. {StudioUi.MayNotWork}", out float typeScale)) continue;
                int index = part.Index, typeRow = row.Index;
                workspace.Run(() => workspace.ApplyDamageEdit(damage.ConfigTag,
                    (tag, bytes) => CombatantDamage.SetTypeScale(tag, bytes, index, typeRow, typeScale),
                    "Changed damage type scale"));
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawDamagePools(StudioWorkspace workspace, CombatantDamage damage)
    {
        if (damage.Pools.Count == 0 || !ImGui.BeginTable("damage-pools", 2,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("Pool");
        ImGui.TableSetupColumn("Start fraction");
        ImGui.TableHeadersRow();
        foreach (var pool in damage.Pools)
        {
            ImGui.PushID(pool.Index);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextUnformatted(pool.Index.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            if (DamageInput(workspace, $"{damage.ConfigTag}:pool{pool.Index}", pool.Fraction, -1, StudioUi.MayNotWork,
                out float fraction))
            {
                int index = pool.Index;
                workspace.Run(() => workspace.ApplyDamageEdit(damage.ConfigTag,
                    (tag, bytes) => CombatantDamage.SetPoolFraction(tag, bytes, index, fraction),
                    "Changed pool start fraction"));
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    /** A float field that commits once, when editing ends with a new finite value. */
    private bool DamageInput(StudioWorkspace workspace, string key, float current, float width, string tip, out float value)
    {
        value = damageDrafts.TryGetValue(key, out float draft) ? draft : current;
        ImGui.BeginDisabled(!workspace.CanEdit);
        ImGui.SetNextItemWidth(width);
        UiAuto.InputFloat("##" + key, ref value, 0, 0, "%g");
        bool active = ImGui.IsItemActive(), done = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.EndDisabled();
        StudioUi.Tip(workspace.Project is null ? "Open a project to edit." : tip);
        if (active) damageDrafts[key] = value; else damageDrafts.Remove(key);
        return done && float.IsFinite(value) && value != current;
    }
}
