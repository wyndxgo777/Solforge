using SunriseStudio.Formats;

namespace Solforge.Weapons;

/** One donor socket as validation sees it: its native type, how many authored choices it takes (0 when the
 *  socket is disabled) and, when the caller decoded them, the plug hashes the game accepts there. */
public sealed record DonorSocket(int Index, ushort SocketType, int MaxAuthoredChoices, IReadOnlySet<uint>? SupportedPlugs);

/** One investment stat the donor carries or can take, with the raw input bounds of its stat-group curve. */
public sealed record DonorStat(ushort DefinitionIndex, string Name, int Value, int? Minimum, int? Maximum);

/** The parts of a stock weapon a recipe is checked against. Built from Studio's investment catalog
 *  (InvestmentItemDetail) or by hand in checks; package reading stays outside this library. */
public sealed record WeaponDonorProfile(string Name, uint ItemHash, RecipeInventorySlot? EquipmentSlot,
    IReadOnlyList<DonorSocket> Sockets, IReadOnlyList<DonorStat> Stats)
{
    /** Installed stat definitions the donor does not carry but a recipe may add. */
    public IReadOnlyList<DonorStat> AddableStats { get; init; } = [];

    /** Reads a donor from Studio's investment detail. plugHash maps a socket's allowed plug template index to
     *  the plug item hash a recipe names; without it plug membership is not checked (MissingSupportedPlugSet). */
    public static WeaponDonorProfile FromInvestment(InvestmentItemDetail detail, Func<ushort, uint?>? plugHash = null)
    {
        var item = detail.Item;
        RecipeInventorySlot? slot = item.Bucket switch
        {
            0 => RecipeInventorySlot.Kinetic, 1 => RecipeInventorySlot.Energy, 2 => RecipeInventorySlot.Power, _ => null,
        };
        var sockets = detail.Sockets.Select(socket => new DonorSocket(socket.Index, socket.Type,
            socket.Type == ushort.MaxValue ? 0 : WeaponRecipe.MaxSocketChoices,
            plugHash is null ? null : socket.AllowedPlugs.Select(plugHash).OfType<uint>().ToHashSet())).ToList();
        var stats = detail.Stats.Select(stat =>
        {
            int? minimum = null, maximum = null;
            if (detail.DisplayCurves.TryGetValue(stat.Index, out var curve) && curve.Points.Count > 0)
            {
                minimum = curve.Points.Min(point => point.Input);
                maximum = curve.Points.Max(point => point.Input);
            }
            return new DonorStat(stat.Index, stat.Name, stat.Value, minimum, maximum);
        }).ToList();
        return new(item.Name, item.Hash, slot, sockets, stats);
    }
}
