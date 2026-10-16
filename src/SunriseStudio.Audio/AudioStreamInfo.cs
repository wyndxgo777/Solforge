/** File: Format summary of an audio stream. */
namespace SunriseStudio.Audio;

/** Sample rate, channels, sample count and encoding of a stream. */
public sealed record AudioStreamInfo(int SampleRate, int Channels, long Samples, string Encoding)
{
    public double DurationSeconds => SampleRate > 0 ? (double)Samples / SampleRate : 0;
}
