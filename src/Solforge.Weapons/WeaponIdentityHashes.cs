using System.Text;

namespace Solforge.Weapons;

/** Parhelion's WeaponCloneIdentity::from_namespace: the twelve identity hashes a namespace derives, in
 *  allocation order. The key keeps Parhelion's "parhelion/" prefix for every namespace, so a recipe
 *  imported from Parhelion derives the same hashes it had there. */
public static class WeaponIdentityHashes
{
    /** Parhelion's LOCALIZATION_DONOR_STRING_HASHES. Every localized hash must sort after the terminal one. */
    public const uint DonorStringBankFirst = 0x2DF71B81, DonorStringBankTerminal = 0x304D56FE;

    /** Allocation roles in order; index 4 onward are localized string hashes. */
    public static readonly IReadOnlyList<string> Roles =
    [
        "item", "collectible", "unlock", "pattern_global", "name", "type", "flavor", "source",
        "collection_name", "collection_description", "inventory_hint", "collection_requirement",
    ];
    public const int FirstLocalizedRole = 4;
    /** The recipe field of each role, in Roles order. */
    public static readonly IReadOnlyList<string> Fields =
        [.. Roles.Select(role => role == "pattern_global" ? "identity.pattern_global_id_hash" : $"identity.{role}_hash")];

    /** The twelve hashes for a namespace, in Roles order. Throws when the namespace is malformed. */
    public static uint[] Derive(string ns)
    {
        if (WeaponNamespace.Problem(ns) is { } problem) throw new WeaponRecipeException(problem);
        var occupied = new HashSet<uint> { DonorStringBankFirst, DonorStringBankTerminal };
        var hashes = new uint[Roles.Count];
        for (int i = 0; i < hashes.Length; i++)
            hashes[i] = Allocate(ns, Roles[i], occupied, i >= FirstLocalizedRole ? DonorStringBankTerminal : null);
        return hashes;
    }

    private static uint Allocate(string ns, string role, HashSet<uint> occupied, uint? minimumExclusive)
    {
        for (int nonce = 0; nonce <= ushort.MaxValue; nonce++)
        {
            uint hash = Fnv1(nonce == 0 ? $"parhelion/{ns}/{role}" : $"parhelion/{ns}/{role}/{nonce}");
            if (hash != 0 && hash != WeaponRecipe.ReservedEmptyNameHash
                && (minimumExclusive is not { } minimum || hash > minimum) && occupied.Add(hash))
                return hash;
        }
        throw new WeaponRecipeException($"Could not allocate a unique {role} hash for namespace \"{ns}\"");
    }

    /** FNV-1 over the UTF-8 bytes with ASCII upper case folded: basis 0x811C9DC5, multiply by 0x01000193 then xor. */
    public static uint Fnv1(string name)
    {
        uint hash = WeaponRecipe.ReservedEmptyNameHash;
        foreach (byte value in Encoding.UTF8.GetBytes(name))
            hash = unchecked(hash * 0x01000193) ^ (value is >= (byte)'A' and <= (byte)'Z' ? (uint)(value + 32) : value);
        return hash;
    }
}
