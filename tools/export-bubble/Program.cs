using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Solforge.Retail;
using Solforge.Retail.Porting;

namespace Solforge.Tools.ExportBubble;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== SolForge Europa Bubble Geometry Exporter ===");
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: dotnet run -- <bubble_tag_hex> [target_directory] [max_meshes]");
            Console.WriteLine("Example: dotnet run -- 0x810E98BA \"C:\\output\\beyond\" 500");
            return 1;
        }

        string tagHex = args[0];
        uint? bubbleTag = RetailTag.Parse(tagHex);
        if (bubbleTag is null)
        {
            Console.Error.WriteLine($"Error: Invalid tag '{tagHex}'");
            return 1;
        }

        string targetDir = args.Length >= 2 
            ? args[1] 
            : Path.Combine(Directory.GetCurrentDirectory(), "exported_bubbles", bubbleTag.Value.ToString("x8"));
        
        int maxMeshes = args.Length >= 3 && int.TryParse(args[2], out int mm) ? mm : 1000;

        Console.WriteLine($"Target Bubble: {RetailTag.Hex(bubbleTag.Value)}");
        Console.WriteLine($"Target Staging Directory: {targetDir}");
        Console.WriteLine($"Max Meshes: {maxMeshes}");

        var options = RetailOptions.FromEnvironment();
        Console.WriteLine($"Retail Root: {options.Root}");

        using var source = new RetailSource(options);
        source.Progress = line => Console.WriteLine($"[Index Progress] {line}");

        Console.WriteLine("Loading retail tag index...");
        bool indexLoaded = await source.WaitForIndexAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        if (!indexLoaded || source.Index is null)
        {
            Console.Error.WriteLine($"Failed to load retail index: {source.IndexError}");
            return 1;
        }
        Console.WriteLine($"Index loaded successfully. Total entries: {source.Index.Count}");

        string tempStagingRoot = Path.Combine(Path.GetTempPath(), "sol_bubble_export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempStagingRoot);

        try
        {
            Console.WriteLine($"Exporting geometry for bubble {RetailTag.Hex(bubbleTag.Value)}...");
            var result = RetailGeometryExport.Export(
                source,
                bubbleTag.Value,
                tempStagingRoot,
                statics: true,
                terrain: true,
                maxMeshes: maxMeshes,
                CancellationToken.None
            );

            Console.WriteLine("\n=== Export Summary ===");
            Console.WriteLine($"Static Meshes: {result.Meshes}");
            Console.WriteLine($"Terrain Meshes: {result.TerrainMeshes}");
            Console.WriteLine($"Total Triangles: {result.Triangles:N0}");
            Console.WriteLine($"Total Placed Instances: {result.Instances:N0}");
            Console.WriteLine($"Skipped Meshes: {result.Skipped}");
            Console.WriteLine($"Files Produced: {result.Files.Count}");

            if (result.Notes.Count > 0)
            {
                Console.WriteLine("\nExport Notes (first 10):");
                foreach (var note in result.Notes.Take(10))
                {
                    Console.WriteLine($"  - {note}");
                }
            }

            Directory.CreateDirectory(targetDir);
            Console.WriteLine($"\nCopying exported files to target directory: {targetDir}");
            foreach (var file in result.Files)
            {
                string fileName = Path.GetFileName(file);
                string destFile = Path.Combine(targetDir, fileName);
                File.Copy(file, destFile, overwrite: true);
            }

            Console.WriteLine($"Successfully staged {result.Files.Count} files in {targetDir}!");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nExport failed: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempStagingRoot))
                {
                    Directory.Delete(tempStagingRoot, true);
                }
            }
            catch { }
        }
    }
}
