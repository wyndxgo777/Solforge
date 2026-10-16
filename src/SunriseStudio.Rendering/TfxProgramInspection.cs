namespace SunriseStudio.Rendering;

/** One decoded TFX instruction: offset, opcode and operand bytes. This file also disassembles TFX programs. */
public sealed record TfxProgramInstruction(int Offset, byte Opcode, byte[] Operands);
/** An extern read at a given instruction offset. */
public sealed record TfxProgramDependency(int Offset, string Kind, byte Extern, byte Element);
/** Decoded view of a TFX program: instructions, extern dependencies and parameter indices. */
public sealed record TfxProgramSummary(IReadOnlyList<TfxProgramInstruction> Instructions,
    IReadOnlyList<TfxProgramDependency> Dependencies, IReadOnlyList<byte> ParameterIndices,
    bool FullyDecoded, int? UnverifiedOffset, byte? UnverifiedOpcode);

/** Disassembles TFX bytecode into instructions and dependencies without running it. */
public static class TfxProgramInspection
{
    /** Unknown operand lengths stop inspection before later bytes can be misread as instructions. */
    public static TfxProgramSummary Read(ReadOnlySpan<byte> program)
    {
        var instructions = new List<TfxProgramInstruction>();
        var dependencies = new List<TfxProgramDependency>();
        var parameters = new HashSet<byte>();
        for (int offset = 0; offset < program.Length;)
        {
            int start = offset;
            byte opcode = program[offset++];
            int operands = opcode switch
            {
                0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x09 or 0x0B or 0x0C or 0x0D
                    or 0x0A or 0x0E or 0x0F or 0x10 or 0x12 or 0x13 or 0x15 or 0x17 or 0x18 or 0x19 or 0x1A or 0x1C or 0x1D or 0x1F or 0x20 or 0x21 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x29 or 0x2A or 0x2E => 0,
                0x22 or 0x34 or 0x35 or 0x37 or 0x38 or 0x39 or 0x3A or 0x3B
                    or 0x42 or 0x43 or 0x44 or 0x45 or 0x46 or 0x47 or 0x49
                    or 0x4C or 0x4D or 0x4E => 1,
                0x3C or 0x3D or 0x3E or 0x3F => 2,
                _ => -1
            };
            if (operands < 0)
                return new(instructions, dependencies, parameters.Order().ToArray(), false, start, opcode);
            if (offset > program.Length - operands)
                throw new InvalidDataException($"TFX instruction at +0x{start:X} has truncated operands.");
            var bytes = program.Slice(offset, operands).ToArray();
            instructions.Add(new(start, opcode, bytes));
            if (opcode is >= 0x3C and <= 0x3F)
                dependencies.Add(new(start, opcode switch
                { 0x3C => "Float", 0x3D => "Vector", 0x3E => "Matrix", _ => "Resource" }, bytes[0], bytes[1]));
            if (opcode == 0x4E) parameters.Add(bytes[0]);
            offset += operands;
        }
        return new(instructions, dependencies, parameters.Order().ToArray(), true, null, null);
    }
}
