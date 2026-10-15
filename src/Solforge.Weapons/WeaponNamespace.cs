using System.Text;

namespace Solforge.Weapons;

/** The authoring namespace that prefixes every hash a recipe owns. Solforge recipes use "solforge.";
 *  recipes written by Parhelion keep their "parhelion." prefix so they can be imported unchanged. */
public static class WeaponNamespace
{
    public const string Prefix = "solforge.", LegacyPrefix = "parhelion.";
    public const int MaximumLength = 64;

    /** Null when the namespace is well formed, otherwise the problem in Parhelion's words. */
    public static string? Problem(string? ns)
    {
        if (ns is null) return "Weapon namespace is missing";
        string? suffix = ns.StartsWith(Prefix, StringComparison.Ordinal) ? ns[Prefix.Length..]
            : ns.StartsWith(LegacyPrefix, StringComparison.Ordinal) ? ns[LegacyPrefix.Length..] : null;
        if (string.IsNullOrEmpty(suffix))
            return $"Weapon namespace \"{ns}\" must begin with \"{Prefix}\" (or \"{LegacyPrefix}\" for imported recipes) and include a name";
        if (ns.Length > MaximumLength || !suffix.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.'))
            return $"Weapon namespace \"{ns}\" must be at most {MaximumLength} characters and use lowercase ASCII letters, digits, '.', '-', or '_'";
        return null;
    }

    /** The canonical namespace for a weapon name: the prefix plus the name's slug. */
    public static string ForWeaponName(string name)
    {
        string slug = Slug(name);
        if (slug.Length == 0) throw new WeaponRecipeException("Weapon name must contain at least one ASCII letter or digit");
        int maximum = MaximumLength - Prefix.Length;
        if (slug.Length > maximum)
            throw new WeaponRecipeException($"Weapon name produces a namespace longer than {MaximumLength} characters; shorten it to at most {maximum} ASCII letters or digits");
        return Prefix + slug;
    }

    /** Lower-case ASCII letters and digits, runs of anything else collapsed to one '-', at most 80 characters. */
    public static string Slug(string text)
    {
        var slug = new StringBuilder(text.Length);
        bool separatorPending = false;
        foreach (char c in text)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (separatorPending && slug.Length > 0) slug.Append('-');
                separatorPending = false;
                slug.Append(char.ToLowerInvariant(c));
            }
            else separatorPending = true;
        }
        if (slug.Length > 80) slug.Length = 80;
        return slug.ToString();
    }
}
