using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solforge.Weapons;

/** Reads and writes .weapon.json. Names are snake_case, unknown fields are rejected (Parhelion's
 *  deny_unknown_fields), nulls are left out on save, and every load and save validates the recipe. */
public static class WeaponRecipeJson
{
    public const string Extension = ".weapon.json";

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    /** Parses, checks the schema version, puts stats and locales in saved order and validates. */
    public static WeaponRecipe Parse(string json)
    {
        WeaponRecipe? recipe;
        try { recipe = JsonSerializer.Deserialize<WeaponRecipe>(json, Options); }
        catch (JsonException error) { throw new WeaponRecipeException($"Invalid weapon recipe JSON: {error.Message}", error); }
        if (recipe is null) throw new WeaponRecipeException("Invalid weapon recipe JSON: the document is null");
        if (recipe.Schema != WeaponRecipe.CurrentSchema)
            throw new WeaponRecipeException($"Unsupported recipe schema {recipe.Schema}; expected {WeaponRecipe.CurrentSchema}");
        recipe = recipe.Canonical();
        WeaponRecipeValidator.ThrowIfInvalid(recipe);
        return recipe;
    }

    /** Validates and writes the canonical form. */
    public static string Serialize(WeaponRecipe recipe)
    {
        WeaponRecipeValidator.ThrowIfInvalid(recipe);
        return JsonSerializer.Serialize(recipe.Canonical(), Options);
    }

    public static WeaponRecipe Load(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new WeaponRecipeException($"Could not read {path}: {error.Message}", error);
        }
        return Parse(json);
    }

    /** Writes through a temporary file so a failed save never leaves half a recipe behind. */
    public static void Save(WeaponRecipe recipe, string path)
    {
        string json = Serialize(recipe), temporary = path + ".tmp";
        File.WriteAllText(temporary, json + "\n");
        File.Move(temporary, path, overwrite: true);
    }
}
