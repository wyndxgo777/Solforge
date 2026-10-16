using System.Reflection;
using SunriseStudio.Editor;
using SunriseStudio.Formats;

/** Checks that binding operands edited through the UI text round-trip bit-exactly when unchanged. */
internal static class CuiMutationUiChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run()
    {
        const BindingFlags access = BindingFlags.Static | BindingFlags.NonPublic;
        var format = typeof(CuiPage).GetMethod("Operand", access)!;
        var parse = typeof(CuiPage).GetMethod("ParseOperand", access)!;
        uint[] values = [0x3F800001, 0x3EAAAAAB, 0x00000001, 0x7F7FFFFF, 0x80000000, 0xC248F5C3];
        foreach (uint bits in values)
        {
            var transform = new CuiBindingTransform(CuiBindingTransformKind.CompareFloat, Operand: bits);
            var text = (string)format.Invoke(null, [transform])!;
            uint parsed = (uint)parse.Invoke(null, [transform.Kind, text])!;
            if (parsed != bits)
                throw new InvalidOperationException($"Unchanged binding operand changed {bits:X8} to {parsed:X8} through '{text}'.");
        }
        Console.WriteLine("cui_mutation_ui float_operand=bit_exact");
    }
}
