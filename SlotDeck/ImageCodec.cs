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

/// <summary>How fast a GIF plays: its own speed slowed by a percentage, or a fixed time between frames.</summary>
public sealed record GifTiming(int SlowPercent = 0, int? FrameMs = null)
{
    public static readonly GifTiming Original = new();

    /// <summary>The delay the keyboard gets, given the GIF's own average delay per kept frame.</summary>
    public int DelayFor(int baseMs) =>
        Math.Clamp(FrameMs ?? (int)Math.Round(baseMs * (100 + SlowPercent) / 100.0), ImageCodec.MinDelayMs, ushort.MaxValue);
}

/// <summary>Converts between picture files and the keyboard's RGB565 image packages.</summary>
public static class ImageCodec
{
    public const int W = ScreenFiles.Width, H = ScreenFiles.Height;
    /// <summary>Longer GIFs keep every n-th frame so a write stays around a minute or two.</summary>
    public const int MaxFrames = 100;
    public const int MinDelayMs = 10;
    /// <summary>Size of one frame on the keyboard (type tag, BMP header, RGB565 rows, timestamp).</summary>
    public const int PackageBytes = ScreenFiles.TypeTagSize + 54 + W * 2 * H + 4;

    /// <summary>Opens a picture without keeping the file locked.</summary>
    public static Image Open(string path) => Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));

    public static int FrameCount(Image img) =>
        img.FrameDimensionsList.Contains(FrameDimension.Time.Guid) ? img.GetFrameCount(FrameDimension.Time) : 1;

    /// <summary>The source frames that go to the keyboard: all of them, or every n-th for long GIFs.</summary>
    public static int[] KeptFrames(int count)
    {
        int step = (int)Math.Ceiling(count / (double)MaxFrames);
        return Enumerable.Range(0, (count + step - 1) / step).Select(i => i * step).ToArray();
    }

    /// <summary>The GIF's own speed, as one delay per kept frame: its total time spread evenly (like Web Hub).</summary>
    public static int BaseDelayMs(Image img)
    {
        int count = FrameCount(img);
        return (int)Math.Round(ReadDelays(img, count).Sum() / (double)KeptFrames(count).Length);
    }

    /// <summary>The whole picture: on the screen it gets black bars where its shape differs.</summary>
    public static RectangleF WholePicture(Image img) => new(0, 0, img.Width, img.Height);

    /// <summary>The largest screen-shaped area in the middle of the picture: fills the screen, no bars.</summary>
    public static RectangleF FillScreen(Image img)
    {
        float w = img.Width, h = img.Height;
        if (w / h > W / (float)H) w = h * W / H; else h = w * H / W;
        return new RectangleF((img.Width - w) / 2, (img.Height - h) / 2, w, h);
    }

    /// <summary>Loads an image or GIF and turns the chosen area into screen frames (whole picture if none).</summary>
    public static ScreenContent Load(string path, RectangleF? area = null, GifTiming? timing = null)
    {
        using var img = Open(path);
        var crop = area ?? WholePicture(img);
        int count = FrameCount(img);
        var packages = new List<byte[]>();
        foreach (int i in KeptFrames(count))
        {
            if (count > 1) img.SelectActiveFrame(FrameDimension.Time, i);
            using var frame = Render(img, crop);
            packages.Add(ScreenFiles.ImagePackage(ToRgba(frame), W, H));
        }
        int delay = packages.Count > 1 ? (timing ?? GifTiming.Original).DelayFor(BaseDelayMs(img)) : 0;
        return new ScreenContent(packages, delay, count);
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

    /// <summary>Draws the area of the current frame onto a 248x170 screen, centred with black bars around it.</summary>
    public static Bitmap Render(Image img, RectangleF area)
    {
        var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float s = Math.Min(W / area.Width, H / area.Height);
        int dw = Math.Clamp((int)Math.Round(area.Width * s), 1, W), dh = Math.Clamp((int)Math.Round(area.Height * s), 1, H);
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY); // no dark seams at the edges
        g.DrawImage(img, new Rectangle((W - dw) / 2, (H - dh) / 2, dw, dh), area.X, area.Y, area.Width, area.Height, GraphicsUnit.Pixel, attrs);
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
        // The BMP's pixel offset counts from the BMP, which starts after the type tag. Reading from there shows
        // what the keyboard shows, including the 1 px shift in pictures Web Hub wrote.
        int offset = ScreenFiles.TypeTagSize + (int)BinaryPrimitives.ReadUInt32LittleEndian(pkg.AsSpan(12));
        if (w is < 1 or > 1024 || h is < 1 or > 1024 || offset is < 56 or > 1026) return null;
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
