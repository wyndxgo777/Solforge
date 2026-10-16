using System.Globalization;
using ImGuiNET;
using SunriseStudio.Formats;

namespace SunriseStudio.Editor;

public sealed partial class CuiPage
{
    private static readonly string[] ComparisonSymbols = ["==", "!=", "<", "<=", ">=", ">"];
    private static readonly string VisibilityPath = CuiBindingPreview.VisibilitySelector.ToString("X");
    private CuiComposition? bindingScene;
    private IReadOnlyList<CuiComposedBinding> bindingRows = [];
    private HashSet<(ushort Node, string Path)> bindingTargets = [];
    private int editingBinding = -1, bindingComparison;
    private string bindingOperand = "";
    private readonly Dictionary<(ushort Node, string Path), CuiPreviewValue> bindingInputs = [];
    private IReadOnlyDictionary<(ushort Node, string Path), CuiPreviewValue> bindingValues = new Dictionary<(ushort, string), CuiPreviewValue>();
    private HashSet<int> bindingHidden = [];
    private uint bindingInputsHierarchy;
    private bool applyBindings, bindingValuesStale = true;

    /** Bindings of every placed resource, preview inputs and computed values follow the open hierarchy. */
    private void RefreshBindings(CuiComposition scene)
    {
        if (document is null) { bindingScene = null; bindingRows = []; bindingHidden = []; return; }
        if (bindingInputsHierarchy != document.TagHash)
        { bindingInputs.Clear(); bindingInputsHierarchy = document.TagHash; bindingValuesStale = true; }
        if (!ReferenceEquals(bindingScene, scene))
        {
            bindingScene = scene; bindingRows = CuiComposedBindings.Read(scene); editingBinding = -1; bindingValuesStale = true;
            bindingTargets = bindingRows.Where(row => row.Composed.Error.Length == 0)
                .Select(row => (row.Composed.Target.Node, row.Composed.Target.PathKey)).ToHashSet();
        }
        if (!bindingValuesStale) return;
        bindingValuesStale = false;
        bindingValues = CuiBindingPreview.Evaluate(bindingRows.Select(row =>
            preview?.ConfiguredComparisons.TryGetValue((row.Instance.Resource, row.Local.Offset), out var configured) == true
                ? row.Composed with { Transform = configured } : row.Composed).ToArray(), bindingInputs);
        bindingHidden = [];
        foreach (var node in scene.Nodes)
            if (bindingValues.TryGetValue(((ushort)node.Id, VisibilityPath), out var value) && value is { Kind: CuiValueKind.Bool, AsBool: false }
                && node.NativeTypes.HasLayout(node.Definition.TypeHash, node.Definition.AuxiliaryTypeHash)) bindingHidden.Add(node.Id);
        var children = scene.Nodes.GroupBy(node => node.ParentId).ToDictionary(group => group.Key, group => group.Select(node => node.Id).ToArray());
        var pending = new Stack<int>(bindingHidden);
        while (pending.TryPop(out int hidden))
            if (children.TryGetValue(hidden, out var nested))
                foreach (int child in nested) if (bindingHidden.Add(child)) pending.Push(child);
    }

    /** Lists bindings that read or write the selected widget; root comparisons are editable. */
    private void DrawBindings(StudioPageContext context, CuiComposition scene, CuiComposedNode selected)
    {
        var rows = Enumerable.Range(0, bindingRows.Count).Where(index =>
            bindingRows[index].Composed.Source.Node == selected.Id || bindingRows[index].Composed.Target.Node == selected.Id).ToArray();
        if (rows.Length == 0) return;
        ImGui.SeparatorText($"Bindings ({rows.Length})");
        foreach (int position in rows)
        {
            var row = bindingRows[position];
            var binding = preview?.ConfiguredComparisons.TryGetValue((row.Instance.Resource, row.Local.Offset), out var configured) == true
                ? row.Composed with { Transform = configured } : row.Composed;
            ImGui.PushID(position);
            bool reads = binding.Target.Node == selected.Id;
            var other = reads ? binding.Source : binding.Target;
            ImGui.TextDisabled(reads ? "from" : "to"); ImGui.SameLine();
            var node = scene.Nodes.FirstOrDefault(candidate => candidate.Id == other.Node);
            if (UiAuto.SmallButton(node is null ? $"Node {other.Node}" : StructureName(node)) && node is not null)
            { selectedComposed = node.Id; revealComposed = true; }
            ImGui.SameLine(); ImGui.TextUnformatted(Describe(binding));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{context.Workspace.DisplayName(row.Instance.Resource)} #{row.Local.Index}\n"
                    + $"{binding.Source.Node}:{binding.Source.PathKey} -> {binding.Target.Node}:{binding.Target.PathKey}");
            if (bindingValues.TryGetValue((binding.Target.Node, binding.Target.PathKey), out var result))
            { ImGui.SameLine(); ImGui.TextColored(StudioTheme.Accent, "= " + Format(result)); }
            if (binding.Error.Length != 0) ImGui.TextDisabled(binding.Error);
            else
            {
                DrawInput(binding);
                if (row.Instance.Root && binding.Transform.Kind is not (CuiBindingTransformKind.None or CuiBindingTransformKind.Not))
                    DrawComparisonEditor(context, scene, selected, row, position);
            }
            ImGui.PopID();
        }
    }

    /** Only a source no binding drives can take an explicit preview value. */
    private void DrawInput(CuiBinding row)
    {
        var key = (row.Source.Node, row.Source.PathKey);
        if (CuiBindingPreview.SourceKind(row) is not { } kind || bindingTargets.Contains(key)) return;
        ImGui.SameLine();
        if (!bindingInputs.TryGetValue(key, out var value))
        {
            if (UiAuto.SmallButton("Input")) { bindingInputs[key] = new(kind, 0); bindingValuesStale = true; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Set the source value for the binding preview");
            return;
        }
        uint bits = value.Bits;
        switch (kind)
        {
            case CuiValueKind.Bool:
                bool flag = value.AsBool;
                if (UiAuto.Checkbox("##input", ref flag)) bits = flag ? 1u : 0u;
                break;
            case CuiValueKind.Integer:
                int number = unchecked((int)bits); ImGui.SetNextItemWidth(90);
                if (UiAuto.InputInt("##input", ref number)) bits = unchecked((uint)number);
                break;
            case CuiValueKind.Float:
                float real = BitConverter.UInt32BitsToSingle(bits); ImGui.SetNextItemWidth(90);
                if (UiAuto.InputFloat("##input", ref real) && float.IsFinite(real)) bits = BitConverter.SingleToUInt32Bits(real);
                break;
            default:
                string text = bits.ToString("X8", CultureInfo.InvariantCulture); ImGui.SetNextItemWidth(90);
                if (UiAuto.InputText("##input", ref text, 8, ImGuiInputTextFlags.CharsHexadecimal)
                    && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash)) bits = hash;
                break;
        }
        if (bits != value.Bits) { bindingInputs[key] = value with { Bits = bits }; bindingValuesStale = true; }
        ImGui.SameLine();
        if (UiAuto.SmallButton("x##clear")) { bindingInputs.Remove(key); bindingValuesStale = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear input");
    }

    private static string Format(CuiPreviewValue value) => value.Kind switch
    {
        CuiValueKind.Bool => value.AsBool ? "true" : "false",
        CuiValueKind.Float => BitConverter.UInt32BitsToSingle(value.Bits).ToString("0.###", CultureInfo.InvariantCulture),
        CuiValueKind.Hash => $"0x{value.Bits:X8}",
        _ => unchecked((int)value.Bits).ToString(CultureInfo.InvariantCulture)
    };

    /** Imported resources are shared by other screens, so only the screen's own root is edited here. */
    private void DrawComparisonEditor(StudioPageContext context, CuiComposition scene, CuiComposedNode selected,
        CuiComposedBinding row, int position)
    {
        ImGui.SameLine();
        if (context.Workspace.ConfigBindingAt(row.Instance.Resource, SunriseStudio.Projects.ConfigBindingLimits.ComparisonProfile,
            row.Local.Offset) is { } configured)
        {
            ConfigLabel(context, configured);
            return;
        }
        ImGui.BeginDisabled(!context.Workspace.CanEdit || structure is null);
        if (editingBinding != position)
        {
            if (UiAuto.SmallButton("Edit"))
            { editingBinding = position; bindingComparison = (int)row.Local.Transform.Comparison; bindingOperand = Operand(row.Local.Transform); }
            ImGui.EndDisabled(); return;
        }
        ImGui.SetNextItemWidth(60);
        UiAuto.Combo("##comparison", ref bindingComparison, ComparisonSymbols, ComparisonSymbols.Length);
        ImGui.SameLine(); ImGui.SetNextItemWidth(110);
        UiAuto.InputText("##operand", ref bindingOperand, 32);
        ImGui.SameLine();
        if (UiAuto.SmallButton("Apply"))
            SaveComparisonDraft(context, scene, selected, row);
        ImGui.SameLine(); if (UiAuto.SmallButton("Cancel")) editingBinding = -1;
        ImGui.EndDisabled();
    }

    /**
     * @param context Owns the selected document and project.
     * @param scene Composition that supplied the binding.
     * @param selected Widget to retain after publication.
     * @param row Root-local binding being edited.
     * @return True after a successful save clears the draft.
     */
    private bool SaveComparisonDraft(StudioPageContext context, CuiComposition scene, CuiComposedNode selected,
        CuiComposedBinding row)
    {
        var comparison = (CuiComparison)bindingComparison; string text = bindingOperand;
        var root = scene.Resources[row.Instance.Resource];
        context.Workspace.Run(() =>
        {
            byte[] bytes = CuiBindings.EditComparison(root.Data, row.Local, comparison, ParseOperand(row.Local.Transform.Kind, text));
            ApplyStructure(context, new(document!.Data.ToArray(), bytes, (ushort)selected.Id, new HashSet<ushort>()), null, "Saved binding");
        });
        if (context.Workspace.Error.Length != 0) return false;
        editingBinding = -1;
        return true;
    }

    private static string Describe(CuiBinding row)
    {
        string transform = row.Transform.Kind switch
        {
            CuiBindingTransformKind.None => "->",
            CuiBindingTransformKind.Not => "-> not ->",
            _ => $"-> {ComparisonSymbols[(int)row.Transform.Comparison]} {Operand(row.Transform)} ->"
        };
        return $"{Property(row.Source)} {transform} {Property(row.Target)}";
    }

    private static string Property(CuiBindingEndpoint endpoint) => endpoint.Path.Count == 0 ? "?" : $"0x{endpoint.Path[^1]:X}";

    private static string Operand(CuiBindingTransform transform) => transform.Kind switch
    {
        CuiBindingTransformKind.CompareFloat => BitConverter.UInt32BitsToSingle(transform.Operand).ToString("R", CultureInfo.InvariantCulture),
        CuiBindingTransformKind.CompareHash => $"0x{transform.Operand:X8}",
        _ => unchecked((int)transform.Operand).ToString(CultureInfo.InvariantCulture)
    };

    private static uint ParseOperand(CuiBindingTransformKind kind, string text)
    {
        text = text.Trim();
        if (kind == CuiBindingTransformKind.CompareFloat && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            && float.IsFinite(value)) return BitConverter.SingleToUInt32Bits(value);
        if (kind == CuiBindingTransformKind.CompareHash && uint.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash)) return hash;
        if (kind == CuiBindingTransformKind.CompareInteger && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            return unchecked((uint)number);
        throw new InvalidDataException(kind switch
        {
            CuiBindingTransformKind.CompareFloat => "Enter a finite number.",
            CuiBindingTransformKind.CompareHash => "Enter a hexadecimal hash.",
            _ => "Enter a whole number."
        });
    }
}
