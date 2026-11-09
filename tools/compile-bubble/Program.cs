using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solforge.Retail;

namespace Solforge.Tools.CompileBubble;

public static class Program
{
    // Sol 86657 Map Container & Bubble Class Definitions
    public const uint SolBubbleParentClass = 0x80807DAE;     // 80 bytes
    public const uint SolBubbleDefinitionClass = 0x808091E0; // 24 bytes
    public const uint SolMapContainerClass = 0x80808A54;     // 56 bytes
    public const uint SolNodeTableClass = 0x808099D6;        // 24 bytes
    public const uint SolNodeClass = 0x808099D8;             // 144 bytes
    public const uint SolStaticsComponent = 0x808071B3;      // 24 bytes
    public const uint SolTerrainComponent = 0x8080714B;      // 32 bytes

    public const int NodeStride = 144;

    public static int Main(string[] args)
    {
        Console.WriteLine("=== SolForge Europa Map Container & Bubble Compiler (v38 / Build 86657) ===");
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: dotnet run -- <geometry_staging_dir> [output_package_dir] [bubble_tag_hex]");
            Console.WriteLine("Example: dotnet run -- \"../../01-clients/sol-client/Sunrise/mods/sol.beyond-light/geometry/beyond\" \"../../01-clients/sol-client/Sunrise/mods/sol.beyond-light/packages\" 0x810E98BA");
            return 1;
        }

        string stagingDir = Path.GetFullPath(args[0]);
        string placementsFile = Path.Combine(stagingDir, "placements.json");
        if (!File.Exists(placementsFile))
        {
            Console.Error.WriteLine($"Error: placements.json not found in {stagingDir}");
            return 1;
        }

        string outputDir = args.Length >= 2 
            ? Path.GetFullPath(args[1]) 
            : Path.Combine(stagingDir, "..", "..", "packages");
        Directory.CreateDirectory(outputDir);

        uint bubbleTag = args.Length >= 3 && RetailTag.Parse(args[2]) is { } t ? t : 0x810E98BA;
        Console.WriteLine($"Source Geometry Staging: {stagingDir}");
        Console.WriteLine($"Target Output Directory: {outputDir}");
        Console.WriteLine($"Target Bubble Parent:    {RetailTag.Hex(bubbleTag)}");

        Console.WriteLine("Reading placements.json...");
        using var jsonDoc = JsonDocument.Parse(File.ReadAllText(placementsFile));
        var root = jsonDoc.RootElement;

        var placements = new List<(uint meshTag, Matrix4x4 transform)>();
        foreach (var prop in root.EnumerateObject())
        {
            uint meshTag = uint.Parse(prop.Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? prop.Name[2..] : prop.Name, NumberStyles.HexNumber);
            foreach (var transformArray in prop.Value.EnumerateArray())
            {
                float[] m = new float[16];
                int idx = 0;
                foreach (var el in transformArray.EnumerateArray())
                {
                    if (idx < 16) m[idx++] = el.GetSingle();
                }
                var mat = new Matrix4x4(
                    m[0], m[1], m[2], m[3],
                    m[4], m[5], m[6], m[7],
                    m[8], m[9], m[10], m[11],
                    m[12], m[13], m[14], m[15]
                );
                placements.Add((meshTag, mat));
            }
        }

        Console.WriteLine($"Parsed {placements.Count:N0} total world placements.");

        // Split into chunks of at most 2,000 nodes per SMapNodeTable to match 86657 memory allocation boundaries
        const int maxNodesPerTable = 2000;
        int tableCount = (placements.Count + maxNodesPerTable - 1) / maxNodesPerTable;
        Console.WriteLine($"Assembling {tableCount} SMapNodeTable records...");

        var compiledTables = new List<byte[]>();
        for (int tableIndex = 0; tableIndex < tableCount; tableIndex++)
        {
            int start = tableIndex * maxNodesPerTable;
            int count = Math.Min(maxNodesPerTable, placements.Count - start);
            byte[] tableBytes = BuildNodeTable(placements.GetRange(start, count));
            compiledTables.Add(tableBytes);
        }

        // Build SMapContainer (0x80808A54)
        byte[] containerBytes = BuildContainer(compiledTables.Count);

        // Build SBubbleDefinition (0x808091E0)
        byte[] definitionBytes = BuildBubbleDefinition();

        // Build SBubbleParent (0x80807DAE)
        byte[] parentBytes = BuildBubbleParent(bubbleTag);

        // Write container binary artifacts
        string containerBaseName = $"w64_europa_{bubbleTag:x8}";
        string containerPkgPath = Path.Combine(outputDir, $"{containerBaseName}.pkg.bin");
        string sidecarPath = Path.Combine(outputDir, $"{containerBaseName}.merge");

        using (var fs = File.Create(containerPkgPath))
        {
            fs.Write(parentBytes);
            fs.Write(definitionBytes);
            fs.Write(containerBytes);
            foreach (var tb in compiledTables)
            {
                fs.Write(tb);
            }
        }

        var report = new JsonObject
        {
            ["bubble_tag"] = RetailTag.Hex(bubbleTag),
            ["staging_dir"] = stagingDir,
            ["placements_total"] = placements.Count,
            ["node_tables"] = tableCount,
            ["sol_classes"] = new JsonObject
            {
                ["SBubbleParent"] = $"0x{SolBubbleParentClass:X8} (80B)",
                ["SBubbleDefinition"] = $"0x{SolBubbleDefinitionClass:X8} (24B)",
                ["SMapContainer"] = $"0x{SolMapContainerClass:X8} (56B)",
                ["SMapNodeTable"] = $"0x{SolNodeTableClass:X8} (24B)",
                ["SMapNode"] = $"0x{SolNodeClass:X8} (144B)",
                ["StaticsComponent"] = $"0x{SolStaticsComponent:X8} (24B)"
            },
            ["container_output"] = containerPkgPath,
            ["status"] = "compiled_successfully"
        };

        string reportPath = Path.Combine(outputDir, "compile_report.json");
        File.WriteAllText(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"\nSuccessfully compiled Europa bubble {RetailTag.Hex(bubbleTag)} into {containerPkgPath}!");
        Console.WriteLine($"Compile report written to: {reportPath}");
        return 0;
    }

    private static byte[] BuildNodeTable(List<(uint meshTag, Matrix4x4 transform)> nodes)
    {
        // SMapNodeTable header: 24 bytes
        // Array header at +0x08: { class: 0x808099D8, count: nodes.Count, offset: 24 }
        int totalSize = 24 + nodes.Count * NodeStride;
        byte[] buffer = new byte[totalSize];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), SolNodeTableClass);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), (uint)totalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), SolNodeClass);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12, 4), nodes.Count);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16, 4), 24); // array offset

        for (int i = 0; i < nodes.Count; i++)
        {
            var (meshTag, mat) = nodes[i];
            int at = 24 + i * NodeStride;

            // Decompose rotation and translation
            Matrix4x4.Decompose(mat, out _, out Quaternion rot, out Vector3 trans);

            // Quaternion rotation (x, y, z, w) at +0x00
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(at + 0, 4), rot.X);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(at + 4, 4), rot.Y);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(at + 8, 4), rot.Z);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(at + 12, 4), rot.W);

            // Translation (x, y, z, 1.0f) at +0x10
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(at + 16, 4), trans.X);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(at + 20, 4), trans.Y);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(at + 24, 4), trans.Z);
            BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(at + 28, 4), 1.0f);

            // World ID at +0x70
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(at + 0x70, 8), (ulong)(i + 1));

            // StaticsComponent pointer at +0x78 (self-contained inline component payload)
            // Statically bind component class 0x808071B3 and mesh tag
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at + 0x80, 4), SolStaticsComponent);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at + 0x84, 4), meshTag);
        }

        return buffer;
    }

    private static byte[] BuildContainer(int tableCount)
    {
        byte[] buffer = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), SolMapContainerClass);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), 56);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0x28, 4), tableCount);
        return buffer;
    }

    private static byte[] BuildBubbleDefinition()
    {
        byte[] buffer = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), SolBubbleDefinitionClass);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), 24);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), 1); // 1 container
        return buffer;
    }

    private static byte[] BuildBubbleParent(uint bubbleTag)
    {
        byte[] buffer = new byte[80];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), SolBubbleParentClass);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), 80);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0x20, 4), bubbleTag); // name hash
        return buffer;
    }
}
