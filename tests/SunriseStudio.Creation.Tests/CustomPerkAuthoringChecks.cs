using Solforge.Graphs;
using SunriseStudio.Editor.Creation;
using SunriseStudio.Formats;

namespace SunriseStudio.Creation.Tests;

/** Node graph Custom Perks (Studio.md Phase 27) lower to native programs that compile and decompile intact. */
internal static class CustomPerkAuthoringChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run()
    {
        var labels = PerkLabelTable.Installed;
        PerkLabelTable.Installed = new PerkLabelTable(
            [InvestmentItemClone.Fnv1(PerkLowering.PrecisionLabel), InvestmentItemClone.Fnv1(PerkLowering.PlayerLabel)], []);
        try
        {
            var rampage = Decoded(Perk("Sol Rampage", "on_kill", [Step("magazine_percentage", ("percent", "50"))],
                [Step("add_damage_scalar", ("scalar", "1.25"), ("duration", "6"))]));
            var gate = rampage.Activation.Single();
            Check(gate.Primitive == "state-kill" && gate.Children.SingleOrDefault()?.Primitive == "kill"
                && Value(gate, "property") == InvestmentItemClone.Fnv1(PerkLowering.MagazineProperty)
                && Math.Abs(Value(gate, "maximum") - .5) < 1e-6 && Value(gate.Children[0], "this_weapon") == 1,
                "The magazine gate did not compile to a state-gated kill.");
            Check(rampage.Effects.Single().Primitive == "damage-multiplier" && Math.Abs(Value(rampage.Effects[0], "bonus") - .25) < 1e-6,
                "The damage scalar did not compile to a damage multiplier bonus.");
            Check(rampage.Deactivation.Single().Primitive == "timer" && Math.Abs(Value(rampage.Deactivation[0], "seconds") - 6) < 1e-4,
                "The buff duration did not compile to the deactivation timer.");

            var precise = Decoded(Perk("Sol Focus", "on_precision_hit", [], [Step("grant_super_energy", ("percent", "4"))]));
            var hit = precise.Activation.Single();
            Check(hit.Primitive == "damage" && hit.LabelFilters.Single(row => row.Key == "source").AnyOf
                .SequenceEqual([InvestmentItemClone.Fnv1(PerkLowering.PrecisionLabel)]),
                "The precision hit lost its precision label filter.");
            Check(precise.Effects.Single().Primitive == "ability-energy" && Value(precise.Effects[0], "slot") == 1
                && Math.Abs(Value(precise.Effects[0], "amount") - .04) < 1e-6, "Super energy did not compile to an ability energy grant.");

            var reload = Decoded(Perk("Sol Reload", "on_reload", [], [Step("refill_magazine", ("percent", "30"))]));
            Check(reload.Activation.Single().Primitive == "reload" && reload.Effects.Single().Primitive == "refill"
                && Math.Abs(Value(reload.Effects[0], "this_fraction") - .3) < 1e-6, "The reload refill did not compile.");

            var duel = Decoded(Perk("Sol Duel", "on_kill", [Step("target_type", ("target", "Guardian"))], [Step("spawn_orb", ("count", "3"))]));
            Check(duel.Activation.Single().LabelFilters.Single(row => row.Key == "victim").AnyOf
                .SequenceEqual([InvestmentItemClone.Fnv1(PerkLowering.PlayerLabel)]) && Value(duel.Effects.Single(), "count") == 3,
                "The Guardian kill orb perk did not compile.");

            var heal = Lowered(Perk("Sol Heal", "on_kill", [], [Step("heal")]));
            bool refused = false;
            try { CustomPerkAuthoring.BuildBehavior(heal.Native); }
            catch (InvalidDataException) { refused = true; }
            Check(refused, "A heal perk was packed although no native effect heals.");
        }
        finally { PerkLabelTable.Installed = labels; }
    }

    private static PerkBehaviorGraph Decoded(CustomPerkDefinition perk)
    {
        perk = Lowered(perk);
        var graph = CustomPerkAuthoring.BuildBehavior(perk.Native);
        graph.ProgramTag = 0x80AA0001;
        return NativePerkBehavior.Decompile(NativePerkBehavior.Compile(graph));
    }

    private static CustomPerkDefinition Lowered(CustomPerkDefinition perk) => perk with { Native = PerkLowering.Lower(perk) };

    private static CustomPerkDefinition Perk(string name, string trigger, PerkStep[] conditions, PerkStep[] actions) =>
        new(name, "", "", -1, [], [new PerkRule(name, "", trigger, conditions, actions)], "n");

    private static PerkStep Step(string type, params (string Key, string Value)[] values) =>
        new(type, values.ToDictionary(pair => pair.Key, pair => pair.Value));

    private static double Value(PerkBehaviorNode node, string key) =>
        node.Properties.FirstOrDefault(row => row.Key == key)?.Value ?? double.NaN;

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
