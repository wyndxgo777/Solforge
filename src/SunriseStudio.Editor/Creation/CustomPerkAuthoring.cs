using Solforge.Graphs;
using SunriseStudio.Authoring;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;
using System.Text.Json;

namespace SunriseStudio.Editor.Creation;

/** One installed custom perk: its sandbox perk row (-1 for stats-only), the new plug item and the weapon socket. */
public sealed record PreparedCustomPerk(string Name, int PerkIndex, uint? ProgramTag, uint PlugTag, uint PlugHash,
    int PlugIndex, int Socket, string ReplacedPlug);
public sealed record PreparedCustomPerks(uint WeaponTag, IReadOnlyList<PreparedCustomPerk> Perks,
    IReadOnlyList<ProjectAssetAddition> Assets, IReadOnlyDictionary<uint, byte[]> Changes);

/**
 * Packs node graph Custom Perks (Studio.md Phase 27) the way exotic perks are packed: each perk becomes a new
 * sandbox perk (definition, display and runtime-name rows plus a compiled program tag) on a clone of a plug the
 * chosen weapon socket already embeds, with the perk's stats, and that plug becomes the socket's only and initial
 * plug. The server sends plug sandbox perks and stats to the client in the character record perk bank, so a fresh
 * drop (or /perk force on an old copy) carries the perk once the packages and build_data.bin are deployed.
 */
public static class CustomPerkAuthoring
{
    public const ushort TraitSocketType = 92, IntrinsicSocketType = 176;

    /** The native program graph of a lowered perk; throws when the lowering or native validation fails. */
    public static PerkBehaviorGraph BuildBehavior(NativePerkPlan plan)
    {
        if (plan.Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error) is { } error)
            throw new InvalidDataException(error.Message);
        if (plan.IsPassive) throw new InvalidDataException("A stats-only perk has no perk program.");
        var graph = new PerkBehaviorGraph();
        graph.Activation.AddRange(plan.Activation.Select(Node));
        graph.Effects.AddRange(plan.Effects.Select(Node));
        graph.Deactivation.AddRange(plan.Deactivation.Select(Node));
        var problems = NativePerkBehavior.Validate(graph);
        if (problems.Count != 0) throw new InvalidDataException(string.Join("; ", problems));
        return graph;
    }

    private static PerkBehaviorNode Node(NativePerkStep step)
    {
        var node = NativePerkBehavior.CreateNode(step.Primitive);
        Set(node, step.Values);
        var labelled = node;
        if (step.StateProperty is { } property)
        {
            Set(node, new Dictionary<string, double> { ["property"] = InvestmentItemClone.Fnv1(property) });
            labelled = node.Children.FirstOrDefault() ?? throw new InvalidDataException($"{step.Primitive} has no inner condition.");
            Set(labelled, step.InnerValues);
        }
        foreach (var (key, names) in step.AnyLabels)
        {
            var filter = labelled.LabelFilters.FirstOrDefault(row => row.Key == key)
                ?? throw new InvalidDataException($"{labelled.Primitive} has no {key} label filter.");
            filter.AnyOf = [.. names.Select(name => InvestmentItemClone.Fnv1(name))];
        }
        return node;
    }

    private static void Set(PerkBehaviorNode node, IReadOnlyDictionary<string, double> values)
    {
        foreach (var (key, value) in values)
        {
            var property = node.Properties.FirstOrDefault(row => row.Key == key)
                ?? throw new InvalidDataException($"{node.Primitive} has no input {key}.");
            property.Value = value;
        }
    }

    /** Plans every perk on a private catalog; nothing reaches the project until the workspace commits the result. */
    public static PreparedCustomPerks Prepare(StudioProject project, PackageCatalog source, uint weaponTag,
        IReadOnlyList<CustomPerkDefinition> perks, ushort packageId, string? projectRoot = null)
    {
        if (perks.Count == 0) throw new InvalidDataException("The graph has no Custom Perk.");
        foreach (var perk in perks)
        {
            if (perk.Native.Diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error) is { } error)
                throw new InvalidDataException(error.Message);
            if (string.IsNullOrWhiteSpace(perk.Name) || perk.Name.Trim() != perk.Name || perk.Name.Length > 200 || perk.Description.Length > 4000)
                throw new InvalidDataException("Custom perk names need 1 to 200 characters and descriptions at most 4000.");
        }
        if (!PackageWriter.IsNewPackageId(packageId) || source.ContainsInstalledPackage(packageId))
            throw new InvalidDataException("Choose an unused package ID for the custom perks.");
        using var plan = new ExoticWeaponAuthoring.PlanCatalog(source, project, packageId);
        var catalog = plan.Catalog;
        var investment = InvestmentCatalog.Load(catalog);
        var weapon = investment.FindItem(weaponTag) ?? throw new InvalidDataException("Open the target weapon first.");
        if (weapon.Category != InvestmentCategory.Weapons) throw new InvalidDataException("Custom perks go on weapons.");

        var strings = perks.SelectMany(perk => new[] { perk.Name, perk.Description }).Where(text => text.Length != 0)
            .Distinct(StringComparer.Ordinal).Select(text => new StringBankText(StringBankWriter.Hash(text), text)).ToArray();
        if (strings.GroupBy(text => text.Hash).Any(group => group.Count() > 1))
            throw new InvalidDataException("Two perk texts have the same native string hash. Change one text.");
        var banks = ProjectStringBank.Plan(catalog, project, packageId, plan.NextEntry, strings, [], new());
        plan.Add(banks.Additions); plan.Edit(banks.Edits);
        uint bank = checked((uint)banks.BankIndex);
        var bankStrings = LocalizedStrings.LoadBanks(catalog, InvestmentItemClone.ReadBankRows(
            catalog.Read(new InvestmentSources().StringBanks)).Select(row => row.ContainerTag));
        // Precision and Guardian filters compile against the installed label table.
        if (perks.Any(perk => perk.Native.Activation.Any(step => step.AnyLabels.Count != 0)))
            PerkLabelTable.Installed ??= PerkLabelTable.Parse(catalog.Read(PerkLabelTable.Tag))
                ?? throw new InvalidDataException("The installed perk label table cannot be read.");

        byte[] weaponBytes = catalog.Read(weapon.Tag);
        var used = new HashSet<int>();
        var installed = new List<PreparedCustomPerk>();
        foreach (var perk in perks)
        {
            investment = InvestmentCatalog.Load(catalog, bankStrings);
            var socket = ChooseSocket(catalog, investment, weapon.Tag, weaponBytes, perk, used);
            used.Add(socket.Index);
            if (string.Equals(socket.PlugName, perk.Name, StringComparison.Ordinal))
                throw new InvalidDataException($"'{perk.Name}' is already in socket {socket.Index}. Undo it or rename the perk.");
            var templates = investment.SocketPlugTemplates(weapon.Tag, weaponBytes, socket);
            ushort template = templates.Contains(socket.InitialPlug) ? socket.InitialPlug : templates[0];

            int perkIndex = -1;
            uint? programTag = null;
            if (!perk.Native.IsPassive)
                (perkIndex, programTag) = Register(perk);
            investment = InvestmentCatalog.Load(catalog, bankStrings);
            var plug = investment.Items[template];
            byte[] bytes = ExoticWeaponAuthoring.ClearContributions(investment, plug.Tag, catalog.Read(plug.Tag));
            if (perkIndex >= 0) bytes = investment.AddPerk(plug.Tag, bytes, checked((ushort)perkIndex));
            foreach (var (stat, value) in perk.Stats)
            {
                byte index = byte.TryParse(stat, out byte number) ? number
                    : investment.StatDefinitions.FirstOrDefault(row => string.Equals(row.Name, stat, StringComparison.OrdinalIgnoreCase))?.Index
                    ?? throw new InvalidDataException($"'{perk.Name}': no stat called {stat}.");
                bytes = investment.AddStat(plug.Tag, bytes, index, value);
            }
            byte[] display = catalog.Read(plug.DisplayTag);
            display = InvestmentPerkDisplayEdits.Reset(display, investment.ReadItem(plug.Tag, bytes).Perks.Count);
            display = InvestmentItemClone.SetDisplayString(display, InvestmentItemClone.DisplayName, bank, StringBankWriter.Hash(perk.Name));
            display = InvestmentItemClone.SetDisplayString(display, InvestmentItemClone.DisplayFlavor, bank, StringBankWriter.Hash(perk.Description));
            var item = ItemAuthoring.PlanClone(catalog, project.Name, plug, bytes, display, packageId, plan.NextEntry, perk.Name, null);
            plan.Add(item.Additions); plan.Edit(item.Edits);
            if (perk.Icon.Length != 0) AssignIcon(item, perk.Icon, template, perkIndex);

            investment = InvestmentCatalog.Load(catalog, bankStrings);
            socket = investment.ReadItem(weapon.Tag, weaponBytes).Sockets.Single(row => row.Index == socket.Index);
            weaponBytes = investment.AddSocketPlug(weapon.Tag, weaponBytes, socket, checked((ushort)item.ItemIndex), template,
                preserveTemplateCondition: true);
            socket = investment.ReadItem(weapon.Tag, weaponBytes).Sockets.Single(row => row.Index == socket.Index);
            weaponBytes = investment.KeepSocketPlug(weapon.Tag, weaponBytes, socket, checked((ushort)item.ItemIndex));
            installed.Add(new(perk.Name, perkIndex, programTag, item.DefinitionTag, item.DefinitionHash, item.ItemIndex,
                socket.Index, plug.Name));
        }
        plan.Edit(new Dictionary<uint, byte[]> { [weapon.Tag] = weaponBytes });
        return new(weapon.Tag, installed, plan.Assets.ToArray(), plan.Changes.ToDictionary());

        (int Index, uint Tag) Register(CustomPerkDefinition perk)
        {
            var behavior = BuildBehavior(perk.Native);
            investment = InvestmentCatalog.Load(catalog, bankStrings);
            uint gameplayTag = plan.TagAtNextEntry;
            behavior.ProgramTag = gameplayTag;
            byte[] gameplay = NativePerkBehavior.Compile(behavior);
            uint hash = InvestmentItemClone.NewDefinitionHash($"sunrise.perk:{project.Name}:{gameplayTag:X8}:{perk.Name}",
                investment.PerkDefinitions.Select(row => row.Hash).ToHashSet());
            uint runtimeName = InvestmentSandboxPerkAuthoring.NewRuntimeNameHash(
                catalog.Read(InvestmentSandboxPerkAuthoring.RuntimeNameMapTag), $"sunrise.perk.behavior:{project.Name}:{gameplayTag:X8}:{perk.Name}");
            var registered = InvestmentSandboxPerkAuthoring.Register(
                catalog.Read(InvestmentSandboxPerkAuthoring.DefinitionTableTag),
                catalog.Read(InvestmentSandboxPerkAuthoring.DisplayTableTag),
                catalog.Read(InvestmentSandboxPerkAuthoring.RuntimeNameMapTag), hash, runtimeName, gameplayTag,
                bank, StringBankWriter.Hash(perk.Name), StringBankWriter.Hash(perk.Description), SandboxPerkPresentation.Normal,
                displayStockLength: catalog.ReadOriginal(InvestmentSandboxPerkAuthoring.DisplayTableTag).Length);
            plan.Add([new(AssetNames.FileName(perk.Name) + ".perk", gameplay,
                new(packageId, NativePerkBehavior.Class, 8U << 9, perk.Name, plan.NextEntry))]);
            plan.Add([new(AssetNames.FileName(perk.Name) + ".perk.json", JsonSerializer.SerializeToUtf8Bytes(behavior))]);
            plan.Edit(new Dictionary<uint, byte[]>
            {
                [InvestmentSandboxPerkAuthoring.DefinitionTableTag] = registered.Definitions,
                [InvestmentSandboxPerkAuthoring.DisplayTableTag] = registered.Displays,
                [InvestmentSandboxPerkAuthoring.RuntimeNameMapTag] = registered.RuntimeNames
            });
            return (registered.Index, gameplayTag);
        }

        void AssignIcon(ItemCreationPlan item, string path, ushort template, int perkIndex)
        {
            if (!Path.IsPathRooted(path) && projectRoot is not null) path = Path.Combine(projectRoot, path);
            if (!File.Exists(path)) throw new InvalidDataException($"Icon PNG not found: {path}");
            investment = InvestmentCatalog.Load(catalog, bankStrings);
            var current = investment.FindItem(item.DefinitionTag)!;
            var icon = ItemIconAuthoring.Prepare(project, catalog, current, PngInterchange.Read(File.ReadAllBytes(path)), current.Name,
                packageId, plan.NextEntry, plannedAssets: plan.Assets,
                templateProfile: ItemIconAuthoring.ReadProfile(catalog, investment.Items[template]), style: ItemIconStyle.Symbol);
            plan.Add(icon.Assets); plan.Edit(icon.Changes);
            if (perkIndex < 0) return;
            var perk = InvestmentSandboxPerkAuthoring.Read(catalog, perkIndex);
            plan.Edit(new Dictionary<uint, byte[]>
            {
                [InvestmentSandboxPerkAuthoring.DisplayTableTag] = InvestmentSandboxPerkAuthoring.SetIcon(
                    catalog.Read(InvestmentSandboxPerkAuthoring.DisplayTableTag), perkIndex, perk.DefinitionHash, icon.IconIndex)
            });
        }
    }

    /** The perk's socket, else the first free trait socket, else the first free non-intrinsic socket whose plugs carry perks. */
    private static InvestmentSocket ChooseSocket(PackageCatalog catalog, InvestmentCatalog investment, uint tag, byte[] bytes, CustomPerkDefinition perk,
        HashSet<int> used)
    {
        var sockets = investment.ReadItem(tag, bytes).Sockets;
        if (perk.Socket >= 0)
        {
            var socket = sockets.FirstOrDefault(row => row.Index == perk.Socket)
                ?? throw new InvalidDataException($"'{perk.Name}': the weapon has no socket {perk.Socket}; get_item lists them.");
            if (used.Contains(socket.Index)) throw new InvalidDataException($"Two custom perks use socket {socket.Index}.");
            if (investment.SocketPlugTemplates(tag, bytes, socket).Count == 0)
                throw new InvalidDataException($"'{perk.Name}': socket {socket.Index} embeds no plug to copy.");
            return socket;
        }
        var free = sockets.Where(row => !used.Contains(row.Index) && investment.SocketPlugTemplates(tag, bytes, row).Count != 0).ToList();
        return free.FirstOrDefault(row => row.Type == TraitSocketType)
            ?? free.FirstOrDefault(row => row.Type != IntrinsicSocketType
                && investment.SocketPlugTemplates(tag, bytes, row).Any(plug => investment.Items[plug] is var item
                    && investment.ReadItem(item.Tag, catalog.Read(item.Tag)).Perks.Count != 0))
            ?? throw new InvalidDataException($"'{perk.Name}': the weapon has no free trait socket. Set Socket to an index from get_item.");
    }
}
