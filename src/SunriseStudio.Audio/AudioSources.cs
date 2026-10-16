/** File: Lists the codec sources of a version-113 bank's sounds and music tracks. */
using System.Buffers.Binary;

namespace SunriseStudio.Audio;

/** A codec source: media id, whether it is external, and the owning object and plugin ids. */
public sealed record AudioSource(uint MediaId, bool External, uint ObjectId, uint PluginId);

/** Version 113 music tracks carry a counted source list before their clip timeline. */
public static class AudioSources
{
    /** The codec sources of the bank's sounds and music tracks; a music source plug-in throws. */
    public static IReadOnlyList<AudioSource> Read(AudioBank bank)
    {
        if (bank.Version != 113) throw new NotSupportedException("External sources need Wwise bank version 113.");
        var result = new List<AudioSource>();
        foreach (var item in bank.Objects)
        {
            var bytes = item.Data;
            if (item.Type == 2 && bytes.Length >= 9) Source(item, 0);
            else if (item.Type == 11)
            {
                if (bytes.Length < 5) throw new InvalidDataException("Music track source header is truncated.");
                uint count = U32(bytes, 1);
                if (count > (bytes.Length - 5) / 14) throw new InvalidDataException("Music sources leave their track.");
                for (int i = 0; i < count; i++) Source(item, 5 + i * 14);
            }
        }
        return result;

        void Source(AudioBankObject item, int offset)
        {
            uint plugin = U32(item.Data, offset);
            if ((plugin & 0xF) != 1)
            {
                if (item.Type == 11) throw new NotSupportedException("Music source plug-ins need the Wwise engine.");
                return;
            }
            byte stream = item.Data[offset + 4];
            if (stream > 2) throw new InvalidDataException("Unknown music source storage type.");
            result.Add(new(U32(item.Data, offset + 5), stream != 0, item.Id, plugin));
        }
    }

    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
}
