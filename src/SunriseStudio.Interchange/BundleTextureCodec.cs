using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using SunriseStudio.Formats;
using SunriseStudio.Packages;

namespace SunriseStudio.Interchange;

/** Recompression replaces changed blocks only; unchanged native texture bytes stay intact. */
public static class BundleTextureCodec
{
    /** Decodes one mip, array slice and depth slice of a texture to RGBA; throws when the subresource or depth slice does not exist. */
    public static RgbaImage Decode(TextureData texture, int mip = 0, int slice = 0, int depthSlice = 0)
    {
        var sub = texture.Subresources.Single(value => value.MipLevel == mip && value.ArraySlice == slice);
        if (depthSlice < 0 || depthSlice >= sub.Depth) throw new ArgumentOutOfRangeException(nameof(depthSlice));
        var format = BlockFormat(texture.Format);
        if (format is null) return TexturePixels.Decode(texture, mip, slice, depthSlice);
        byte[] data = sub.Data.AsSpan(depthSlice * sub.SlicePitch, sub.SlicePitch).ToArray();
        var decoded = new BcDecoder().DecodeRaw(data, sub.Width, sub.Height, format.Value);
        return new(sub.Width, sub.Height, MemoryMarshal.AsBytes(decoded.AsSpan()).ToArray());
    }

    /** PNG edits retain dimensions, format and every other mip or slice. */
    public static TextureData Apply(TextureData original, RgbaImage image, int mip = 0, int slice = 0, int depthSlice = 0)
    {
        image.Validate();
        var sub = original.Subresources.Single(value => value.MipLevel == mip && value.ArraySlice == slice);
        if (image.Width != sub.Width || image.Height != sub.Height)
            throw new InvalidDataException("Texture edits must retain their mip dimensions.");
        var baseline = Decode(original, mip, slice, depthSlice);
        if (original.Format is 80 or 83)
            for (int pixel = 0; pixel < image.Pixels.Length; pixel += 4)
                for (int channel = original.Format == 80 ? 1 : 2; channel < 4; channel++)
                    if (image.Pixels[pixel + channel] != baseline.Pixels[pixel + channel])
                        throw new NotSupportedException("This block format cannot store the edited color channel.");
        if (original.Format == 61)
            for (int pixel = 0; pixel < image.Pixels.Length; pixel += 4)
                if (image.Pixels[pixel] != image.Pixels[pixel + 1] || image.Pixels[pixel] != image.Pixels[pixel + 2]
                    || image.Pixels[pixel + 3] != 255)
                    throw new NotSupportedException("R8 textures need grayscale pixels with opaque alpha.");
        byte[] data = sub.Data.ToArray();
        if (BlockFormat(original.Format) is { } format)
        {
            int blockBytes = original.Format is 71 or 72 or 80 ? 8 : 16;
            var encoder = new BcEncoder(format);
            encoder.OutputOptions.GenerateMipMaps = false;
            encoder.OutputOptions.Quality = CompressionQuality.Balanced;
            for (int by = 0; by < (sub.Height + 3) / 4; by++)
                for (int bx = 0; bx < (sub.Width + 3) / 4; bx++)
                {
                    bool differs = false;
                    byte[] block = new byte[64];
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            int px = Math.Min(sub.Width - 1, bx * 4 + x), py = Math.Min(sub.Height - 1, by * 4 + y);
                            int source = (py * sub.Width + px) * 4;
                            image.Pixels.AsSpan(source, 4).CopyTo(block.AsSpan((y * 4 + x) * 4));
                            differs |= !image.Pixels.AsSpan(source, 4).SequenceEqual(baseline.Pixels.AsSpan(source, 4));
                        }
                    if (!differs) continue;
                    byte[] encoded = encoder.EncodeToRawBytes(block, 4, 4, PixelFormat.Rgba32)[0];
                    if (encoded.Length != blockBytes) throw new InvalidDataException("Texture codec returned an unexpected block size.");
                    encoded.CopyTo(data.AsSpan(depthSlice * sub.SlicePitch + by * sub.RowPitch + bx * blockBytes));
                }
        }
        else if (original.Format is 28 or 29 or 87 or 91)
        {
            for (int y = 0; y < sub.Height; y++)
                for (int x = 0; x < sub.Width; x++)
                {
                    int source = (y * sub.Width + x) * 4, target = depthSlice * sub.SlicePitch + y * sub.RowPitch + x * 4;
                    image.Pixels.AsSpan(source, 4).CopyTo(data.AsSpan(target, 4));
                    if (original.Format is 87 or 91) (data[target], data[target + 2]) = (data[target + 2], data[target]);
                }
        }
        else if (original.Format == 61)
        {
            for (int y = 0; y < sub.Height; y++)
                for (int x = 0; x < sub.Width; x++) data[depthSlice * sub.SlicePitch + y * sub.RowPitch + x] = image.Pixels[(y * sub.Width + x) * 4];
        }
        else throw new NotSupportedException("This texture format needs an edited DDS file.");
        return original with { Subresources = original.Subresources.Select(value => ReferenceEquals(value, sub) ? value with { Data = data } : value).ToArray() };
    }

    private static CompressionFormat? BlockFormat(int format) => format switch
    {
        71 or 72 => CompressionFormat.Bc1WithAlpha,
        74 or 75 => CompressionFormat.Bc2,
        77 or 78 => CompressionFormat.Bc3,
        80 => CompressionFormat.Bc4,
        83 => CompressionFormat.Bc5,
        98 or 99 => CompressionFormat.Bc7,
        _ => null
    };
}
