using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SlotDeck;

/// <summary>A picture ready for the keyboard: one package per frame (one for a still image).</summary>
public sealed record ScreenContent(List<byte[]> Packages, int DelayMs, int SourceFrames)
{
    public bool IsGif => Packages.Count > 1;
    public long Bytes => Packages.Sum(p => (long)p.Length);
}

/// <summary>Converts between picture files and the keyboard's RGB565 image packages.</summary>
public static class ImageCodec
{
    public const int W = ScreenFiles.Width, H = ScreenFiles.Height;
    /// <summary>Longer GIFs keep every n-th frame so a write stays around a minute or two.</summary>
    public const int MaxFrames = 100;

    /// <summary>Loads an image or GIF and scales it to the screen (fill = crop to cover, fit = letterbox).</summary>
    public static ScreenContent Load(string path, bool fit)
    {
        using var img = Image.FromFile(path);
        int count = img.FrameDimensionsList.Contains(FrameDimension.Time.Guid) ? img.GetFrameCount(FrameDimension.Time) : 1;
        int step = (int)Math.Ceiling(count / (double)MaxFrames);
        var packages = new List<byte[]>();
        for (int i = 0; i < count; i += step)
        {
            if (count > 1) img.SelectActiveFrame(FrameDimension.Time, i);
            using var frame = Render(img, fit);
            packages.Add(ScreenFiles.ImagePackage(ToRgba(frame), W, H));
        }
        // The keyboard plays every frame with one delay, like Web Hub: the GIF's total time spread evenly.
        int delay = packages.Count > 1 ? (int)Math.Round(ReadDelays(img, count).Sum() / (double)packages.Count) : 0;
        return new ScreenContent(packages, Math.Clamp(delay, 0, ushort.MaxValue), count);
    }

    /// <summary>Per-frame delays in ms. Browsers play 0-10 ms frames at 100 ms, so we do too.</summary>
    private static int[] ReadDelays(Image img, int count)
    {
        var delays = Enumerable.Repeat(100, count).ToArray();
        try
        {
            var raw = img.GetPropertyItem(0x5100)?.Value; // PropertyTagFrameDelay, 1/100 s per frame
            for (int i = 0; raw != null && i < count && i * 4 + 4 <= raw.Length; i++)
            {
                int d = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * 4)) * 10;
                delays[i] = d <= 10 ? 100 : d;
            }
        }
        catch (ArgumentException) { /* no delay tag */ }
        return delays;
    }

    private static Bitmap Render(Image img, bool fit)
    {
        var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float sx = W / (float)img.Width, sy = H / (float)img.Height, s = fit ? Math.Min(sx, sy) : Math.Max(sx, sy);
        int dw = (int)Math.Round(img.Width * s), dh = (int)Math.Round(img.Height * s);
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY); // no dark seams at the edges
        g.DrawImage(img, new Rectangle((W - dw) / 2, (H - dh) / 2, dw, dh), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attrs);
        return bmp;
    }

    private static byte[] ToRgba(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rgba = new byte[bmp.Width * bmp.Height * 4];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, rgba, y * bmp.Width * 4, bmp.Width * 4);
            for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]); // BGRA -> RGBA
            return rgba;
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Turns a file read from the keyboard back into a picture. Returns null if it isn't one.</summary>
    public static Bitmap? Decode(byte[] pkg)
    {
        if (pkg.Length > 2 && pkg[0] != 72)
        {
            // Not a BMP package: Web Hub may store other formats after the 2-byte type header.
            try { using var ms = new MemoryStream(pkg, 2, pkg.Length - 2); using var im = Image.FromStream(ms); return new Bitmap(im); }
            catch { return null; }
        }
        if (pkg.Length < 60) return null;
        int w = BinaryPrimitives.ReadInt32LittleEndian(pkg.AsSpan(20)), h = BinaryPrimitives.ReadInt32LittleEndian(pkg.AsSpan(24));
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(pkg.AsSpan(12));
        if (w is < 1 or > 1024 || h is < 1 or > 1024 || offset is < 54 or > 1024) return null;
        int stride = w * 2 + (4 - w * 2 % 4) % 4;
        if (offset + stride * h > pkg.Length) return null;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++) // rows are stored top to bottom
            {
                for (int x = 0; x < w; x++)
                {
                    ushort px = BinaryPrimitives.ReadUInt16LittleEndian(pkg.AsSpan(offset + y * stride + x * 2));
                    int r = px >> 11 & 31, g = px >> 5 & 63, b = px & 31;
                    row[x * 4] = (byte)(b << 3 | b >> 2);
                    row[x * 4 + 1] = (byte)(g << 2 | g >> 4);
                    row[x * 4 + 2] = (byte)(r << 3 | r >> 2);
                    row[x * 4 + 3] = 255;
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }
}
