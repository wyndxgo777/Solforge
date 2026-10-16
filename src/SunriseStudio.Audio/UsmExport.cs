namespace SunriseStudio.Audio;

/** Writes a USM's streams as files: the video elementary stream, each audio track raw and as WAV, subtitles raw. */
public static class UsmExport
{
    /**
     * @param channel Only this audio channel (and the video); null exports every stream.
     * @return The written paths, video first.
     */
    public static IReadOnlyList<string> Export(byte[] usm, string folder, string name, bool decodeAudio = true, int? channel = null,
        bool includeVideo = true, CancellationToken token = default)
    {
        var info = UsmContainer.Inspect(usm);
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        foreach (var stream in info.Streams)
        {
            token.ThrowIfCancellationRequested();
            if (stream.DataChunks == 0) continue;
            if (stream.Kind == "video" ? !includeVideo : channel is int only && (stream.Kind != "audio" || stream.Channel != only)) continue;
            string path = Path.Combine(folder, $"{name}.{stream.Kind}{stream.Channel}{UsmContainer.Extension(stream)}");
            using (var file = File.Create(path)) UsmContainer.Extract(usm, stream.Signature, stream.Channel, file, token);
            written.Add(path);
            if (!decodeAudio || stream.Kind != "audio" || stream.Codec != "ADX") continue;
            byte[] adx = File.ReadAllBytes(path);
            var header = AdxHeader.Parse(adx);
            string wave = Path.ChangeExtension(path, ".wav");
            AudioStemExport.Write(wave, new PcmWave(header.SampleRate, (ushort)header.Channels, 16, AdxDecoder.Decode(adx, token)));
            written.Add(wave);
        }
        return written;
    }
}
