using System.Buffers.Binary;
using SunriseStudio.Audio;
using static SunriseStudio.ProjectSystem.Tests.Check;

namespace SunriseStudio.ProjectSystem.Tests;

/** Phase 17: WEM media exports as a WAV mix plus one mono WAV stem per channel. */
internal static class StemChecks
{
    internal static void Run(string folder, List<string> checks)
    {
        Directory.CreateDirectory(folder);
        const int rate = 8000, frames = 300;
        byte[] samples = new byte[frames * 2 * 2];
        for (int frame = 0; frame < frames; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(frame * 4), (short)(frame * 7));
            BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(frame * 4 + 2), (short)-(frame * 11));
        }
        // A raw PCM WEM is a little-endian RIFF/WAVE with format tag 1.
        var raw = new PcmWave(rate, 2, 16, samples);
        string source = Path.Combine(folder, "source.wem");
        AudioStemExport.Write(source, raw);
        var written = AudioStemExport.ExportWem(File.ReadAllBytes(source), Path.Combine(folder, "stems"), "fixture");
        Require(written.Select(path => Path.GetFileName(path)).SequenceEqual(new[] { "fixture.wav", "fixture.ch1.wav", "fixture.ch2.wav" }),
            "Stem file names are wrong: " + string.Join(", ", written));
        Require(PcmWave.Read(written[0]).Samples.SequenceEqual(samples), "The WAV mix changed the samples.");
        var left = PcmWave.Read(written[1]);
        var right = PcmWave.Read(written[2]);
        Require(left.Channels == 1 && right.Channels == 1 && left.SampleRate == rate, "Stems are not mono at the source rate.");
        for (int frame = 0; frame < frames; frame++)
        {
            Require(BinaryPrimitives.ReadInt16LittleEndian(left.Samples.AsSpan(frame * 2)) == (short)(frame * 7)
                && BinaryPrimitives.ReadInt16LittleEndian(right.Samples.AsSpan(frame * 2)) == (short)-(frame * 11),
                $"Stem sample {frame} is wrong.");
        }

        var surround = new PcmWave(48000, 6, 16, new byte[6 * 2 * 10]);
        var six = AudioStemExport.ExportWave(surround, Path.Combine(folder, "surround"), "six");
        byte[] header = File.ReadAllBytes(six[0]);
        Require(six.Count == 7 && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22)) == 6
            && BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(40)) == 120, "The 6-channel mix header is wrong.");
        checks.Add("raw PCM WEM exports a WAV mix plus mono stems; 6-channel media writes 7 WAVs");
        Console.WriteLine("skipped: compressed (Wwise Vorbis) WEM stems need game media; manual step in todo.json (Phase 17)");
    }
}
