using System.Numerics;
using System.Text.Json;
using SunriseStudio.Rendering;

/** Checks managed object fog against native reference values within a relative error bound. */
internal static class ObjectFogChecks
{
    /** Compares with native results on the same synthetic inputs, read from a JSON file the caller supplies. */
    public static void Run(string reference)
    {
        using var source = File.OpenRead(reference);
        using var json = JsonDocument.Parse(source);
        float maximumError = 0;
        int cases = 0;
        foreach (var row in json.RootElement.EnumerateArray())
        {
            float[] Values(string name) => row.GetProperty(name).EnumerateArray().Select(value => value.GetSingle()).ToArray();
            Vector3 V3(string name) { var values = Values(name); return new(values[0], values[1], values[2]); }
            Vector4 V4(string name) { var values = Values(name); return new(values[0], values[1], values[2], values[3]); }
            var fog = new NativeViewFog(V3("camera"), V4("scatter"), V4("tint"), 0.7f, 0.25f, 0.5f,
                V4("heightFog"), 0, 2000, row.GetProperty("start").GetSingle(), 0.5f, null);
            var actual = NativeObjectFog.Evaluate(fog, V3("minimum"), V3("maximum"));
            var expected = Values("rows");
            for (int index = 0; index < expected.Length; index++)
            {
                float value = actual[index / 4][index % 4];
                float error = MathF.Abs(value - expected[index]) / MathF.Max(1, MathF.Abs(expected[index]));
                maximumError = MathF.Max(maximumError, error);
                if (!float.IsFinite(value) || error > 2e-5f)
                    throw new InvalidDataException($"Fog case {cases}, word {index}: native {expected[index]}, managed {value}.");
            }
            cases++;
        }
        if (cases == 0) throw new InvalidDataException("No native fog references were supplied.");
        Console.WriteLine($"object_fog native_cases={cases} max_relative_error={maximumError:G6} result=pass");
    }
}
