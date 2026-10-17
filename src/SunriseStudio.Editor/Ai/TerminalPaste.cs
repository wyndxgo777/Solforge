/**
 * File: what a paste or a file drop puts into the AI terminal (TerminalView.cs): quoted file paths for files copied in
 * Explorer or dropped on Solforge, and a temporary PNG for an image on the clipboard (Claude Code takes images by path).
 */
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SunriseStudio.Editor.Ai;

/** One clipboard read: files, text or an image (PNG bytes, or a bitmap still to encode). Exactly one is set. */
internal sealed record ClipboardPaste(IReadOnlyList<string>? Files, string? Text, byte[]? Png, Bitmap? Image);

/** Clipboard and drop helpers for the AI terminal. */
internal static class TerminalPaste
{
    /** Pasted images older than this are removed when the next one is saved. */
    private static readonly TimeSpan Keep = TimeSpan.FromDays(7);

    /** %LOCALAPPDATA%\Solforge\paste, where pasted images are saved. */
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Solforge", "paste");

    /** A path in double quotes, as Windows Terminal and Explorer's "Copy as path" give it; null or blank gives "". */
    internal static string Quote(string? path) => string.IsNullOrWhiteSpace(path) ? "" : "\"" + path.Trim().Trim('"') + "\"";

    /** Quoted paths separated by spaces with a trailing space so typing can follow; no paths gives "". */
    internal static string Paths(IEnumerable<string?> paths)
    {
        var quoted = paths.Select(Quote).Where(text => text.Length > 0).ToList();
        return quoted.Count == 0 ? "" : string.Join(' ', quoted) + " ";
    }

    /**
     * Reads the clipboard on the UI (STA) thread. Files come first (Explorer copy), then text (Office copies text with a
     * picture of it), then the image; the PNG format keeps a screenshot's alpha and skips re-encoding. Null when empty
     * or the clipboard is held by another program.
     */
    internal static ClipboardPaste? Read()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string?>().OfType<string>().ToList();
                if (files.Count > 0) return new ClipboardPaste(files, null, null, null);
            }
            if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (text.Length > 0) return new ClipboardPaste(null, text, null, null);
            }
            if (Clipboard.GetData("PNG") is MemoryStream png && png.Length > 8) return new ClipboardPaste(null, null, png.ToArray(), null);
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                // The clipboard's image can hold a reference to clipboard memory; a copy is safe to encode on another thread.
                var copy = new Bitmap(image);
                image.Dispose();
                return new ClipboardPaste(null, null, null, copy);
            }
        }
        catch (Exception error) when (error is ExternalException or ThreadStateException or ArgumentException or OutOfMemoryException) { }
        return null;
    }

    /** Writes PNG bytes as a new file in the folder and returns its full path; old pastes are pruned. Safe off the UI thread. */
    internal static string SavePng(byte[] png, string folder)
    {
        string path = NewFile(folder);
        File.WriteAllBytes(path, png);
        return path;
    }

    /** Encodes a bitmap as a new PNG in the folder, disposes it and returns the full path. Safe off the UI thread. */
    internal static string SaveImage(Bitmap image, string folder)
    {
        using (image)
        {
            string path = NewFile(folder);
            image.Save(path, ImageFormat.Png);
            return path;
        }
    }

    /** A fresh paste-yyyyMMdd-HHmmss-fff.png path (suffixed when taken) after removing week-old pastes. */
    private static string NewFile(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (string old in Directory.EnumerateFiles(folder, "paste-*.png"))
            try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > Keep) File.Delete(old); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        string stem = "paste-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        string path = Path.Combine(folder, stem + ".png");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem}-{n}.png");
        return Path.GetFullPath(path);
    }
}
