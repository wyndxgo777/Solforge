using System.Runtime.InteropServices;

namespace SunriseStudio.Editor;

/** One rasterized glyph: its alpha bitmap, bounds relative to the origin and horizontal advance. */
internal sealed record CuiBakedGlyph(int Left, int Top, int Width, int Height, float Advance, byte[] Alpha);

/** An isolated DirectWrite factory validates and rasterizes memory fonts without OS installation. */
internal sealed unsafe class CuiDirectWrite : IDisposable
{
    // DirectWrite interface slots include the three IUnknown methods.
    private enum FactorySlot { CreateFace = 9, RegisterLoader = 13, UnregisterLoader = 14, AnalyzeGlyphRun = 30, CreateMemoryLoader = 44 }
    private enum FaceSlot { Metrics = 8, GlyphMetrics = 10, GlyphIndices = 11, Kerning = 25, HasKerning = 26 }
    private const int FileAnalyzeSlot = 5, MemoryReferenceSlot = 4, AlphaBoundsSlot = 3, AlphaTextureSlot = 4;
    // Grayscale natural rendering produces one alpha byte per pixel without grid fitting.
    private const int IsolatedFactory = 1, NaturalSymmetric = 5, NaturalMeasurement = 0, NoGridFit = 1, Grayscale = 1, Alpha1x1 = 0;
    private nint factory, loader;
    private bool registered;

    public CuiDirectWrite()
    {
        try
        {
            Check(DWriteCreateFactory(IsolatedFactory, new Guid("958DB99A-BE2A-4F09-AF7D-65189803D1D3"), out factory));
            nint created = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(factory, (int)FactorySlot.CreateMemoryLoader))(factory, &created));
            loader = created;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(factory, (int)FactorySlot.RegisterLoader))(factory, loader));
            registered = true;
        }
        catch { Dispose(); throw; }
    }

    /**
     * A null owner makes DirectWrite copy the bytes before the managed pin ends.
     * @param bytes Complete font-file bytes.
     * @return An analyzed font face owned by the caller.
     */
    public Face Open(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 12 or > 16 * 1024 * 1024) throw new InvalidDataException("Font payload size is unsupported.");
        nint file = 0, face = 0;
        try
        {
            fixed (byte* data = bytes.Span)
                Check(((delegate* unmanaged[Stdcall]<nint, nint, void*, uint, nint, nint*, int>)Method(loader, MemoryReferenceSlot))
                    (loader, factory, data, (uint)bytes.Length, 0, &file));
            int supported = 0, fileType = 0, faceType = 0; uint faceCount = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, int*, int*, int*, uint*, int>)Method(file, FileAnalyzeSlot))
                (file, &supported, &fileType, &faceType, &faceCount));
            if (supported == 0 || faceCount != 1) throw new NotSupportedException("DirectWrite requires a supported single-face font.");
            Check(((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, uint, int, nint*, int>)Method(factory, (int)FactorySlot.CreateFace))
                (factory, faceType, 1, &file, 0, 0, &face));
            var result = new Face(this, file, face); file = face = 0; return result;
        }
        finally { Release(face); Release(file); }
    }

    /** A font face created from memory bytes; measures kerning and bakes glyph bitmaps at an em size. */
    public sealed class Face : IDisposable
    {
        private readonly CuiDirectWrite owner;
        private nint file, face, face1;
        private readonly ushort units;
        public float LineHeight { get; }
        internal Face(CuiDirectWrite owner, nint file, nint face)
        {
            this.owner = owner; this.file = file; this.face = face;
            FontMetrics metrics = default;
            ((delegate* unmanaged[Stdcall]<nint, FontMetrics*, void>)Method(face, (int)FaceSlot.Metrics))(face, &metrics);
            units = metrics.Units;
            if (units == 0) throw new InvalidDataException("DirectWrite returned invalid font metrics.");
            LineHeight = (metrics.Ascent + metrics.Descent + metrics.LineGap) / (float)units;
            Guid iid = new("a71efdb4-9fdb-4838-ad90-cfc3be8c3daf"); nint extended = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(face, 0))(face, &iid, &extended));
            face1 = extended;
        }

        /** Native text consumes the legacy kern table, without GPOS substitutions or ligatures. */
        public float Kerning(ushort left, ushort right, float emSize)
        {
            ushort* indices = stackalloc ushort[2] { left, right };
            int* adjustments = stackalloc int[2];
            Check(((delegate* unmanaged[Stdcall]<nint, uint, ushort*, int*, int>)Method(face1, (int)FaceSlot.Kerning))
                (face1, 2, indices, adjustments));
            return adjustments[0] * emSize / units;
        }

        /** Maps a Unicode code point to the face's glyph index (0 when missing). */
        public ushort Glyph(uint character)
        {
            ushort glyph = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, uint, ushort*, int>)Method(face, (int)FaceSlot.GlyphIndices))
                (face, &character, 1, &glyph));
            return glyph;
        }

        /**
         * Glyph bounds are checked before the alpha buffer is allocated.
         * @param glyph Validated glyph index.
         * @param emSize Em size in device-independent pixels.
         * @return A grayscale glyph and its design advance.
         */
        public CuiBakedGlyph Bake(ushort glyph, float emSize)
        {
            GlyphMetrics metrics = default;
            Check(((delegate* unmanaged[Stdcall]<nint, ushort*, uint, GlyphMetrics*, int, int>)Method(face, (int)FaceSlot.GlyphMetrics))
                (face, &glyph, 1, &metrics, 0));
            float advance = metrics.AdvanceWidth * emSize / units;
            if (!float.IsFinite(advance) || advance > 4096) throw new InvalidDataException("Font glyph advance exceeds preview limits.");
            long designWidth = metrics.AdvanceWidth - metrics.Left - metrics.Right;
            long designHeight = metrics.AdvanceHeight - metrics.Top - metrics.Bottom;
            if (designWidth < 0 || designHeight < 0 || designWidth * emSize / units > 510 || designHeight * emSize / units > 510)
                throw new InvalidDataException("Font glyph outline exceeds preview dimensions.");
            var run = new GlyphRun { Face = face, EmSize = emSize, Count = 1, Indices = &glyph, Advances = &advance };
            nint analysis = 0;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, GlyphRun*, void*, int, int, int, int, float, float, nint*, int>)
                    Method(owner.factory, (int)FactorySlot.AnalyzeGlyphRun))
                    (owner.factory, &run, null, NaturalSymmetric, NaturalMeasurement, NoGridFit, Grayscale, 0, 0, &analysis));
                Rect bounds = default;
                Check(((delegate* unmanaged[Stdcall]<nint, int, Rect*, int>)Method(analysis, AlphaBoundsSlot))(analysis, Alpha1x1, &bounds));
                int width = checked(bounds.Right - bounds.Left), height = checked(bounds.Bottom - bounds.Top);
                if (width is < 0 or > 512 || height is < 0 or > 512)
                    throw new InvalidDataException("Font glyph exceeds preview dimensions.");
                byte[] alpha = new byte[checked(width * height)];
                if (alpha.Length != 0)
                    fixed (byte* data = alpha)
                        Check(((delegate* unmanaged[Stdcall]<nint, int, Rect*, byte*, uint, int>)Method(analysis, AlphaTextureSlot))
                            (analysis, Alpha1x1, &bounds, data, (uint)alpha.Length));
                return new(bounds.Left, bounds.Top, width, height, advance, alpha);
            }
            finally { Release(analysis); }
        }

        /** Releases the native face and font file. */
        public void Dispose() { Release(face1); face1 = 0; Release(face); face = 0; Release(file); file = 0; }
    }

    /** Releases the native factory and its memory-font loader. */
    public void Dispose()
    {
        if (registered)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(factory, (int)FactorySlot.UnregisterLoader))(factory, loader);
            registered = false;
        }
        Release(loader); loader = 0; Release(factory); factory = 0;
    }

    private static nint Method(nint instance, int slot) => (*(nint**)instance)[slot];
    private static void Release(nint instance)
    {
        if (instance != 0) _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Method(instance, 2))(instance);
    }
    private static void Check(int result)
    {
        if (result < 0) throw new InvalidDataException($"DirectWrite rejected the font operation (0x{result:X8}).");
    }
    [DllImport("dwrite.dll", ExactSpelling = true)]
    private static extern int DWriteCreateFactory(int type, in Guid iid, out nint factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphRun
    {
        public nint Face; public float EmSize; public uint Count; public ushort* Indices;
        public float* Advances; public nint Offsets; public int Sideways; public uint BidiLevel;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FontMetrics
    {
        public ushort Units, Ascent, Descent; public short LineGap; public ushort CapHeight, XHeight;
        public short UnderlinePosition; public ushort UnderlineThickness; public short StrikePosition; public ushort StrikeThickness;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphMetrics
    {
        public int Left; public uint AdvanceWidth; public int Right, Top; public uint AdvanceHeight; public int Bottom, OriginY;
    }
}
