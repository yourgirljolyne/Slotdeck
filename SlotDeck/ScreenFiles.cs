using System.Buffers.Binary;

namespace SlotDeck;

/// <summary>File ids and formats used by the keyboard's screen (from Corsair Web Hub).</summary>
public static class ScreenFiles
{
    public const ushort ResourceMap = 61;   // list of image resources the firmware knows about
    public const ushort ActiveScreen = 62;  // copy of the screen config currently shown

    // TrackDeck's now-playing card lives here (top of Web Hub's user range, 28000-32767).
    // SlotDeck lists it but never writes to it.
    public const ushort TrackDeckConfig = 32700;
    public const ushort TrackDeckImage = 32701;

    public const byte ConfigTypeImage = 56;     // "screen resource config"
    public const byte ConfigTypeGif = 102;

    public const int Width = 248, Height = 170;

    /// <summary>16-byte screen config pointing at an image resource.</summary>
    public static byte[] ImageConfig(ushort resourceId)
    {
        var b = new byte[16];
        b[0] = ConfigTypeImage;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), resourceId);
        return b;
    }

    /// <summary>Bytes before the BMP itself: the file type tag 0x48 0x00.</summary>
    public const int TypeTagSize = 2;

    /// <summary>
    /// Builds an image package: 0x48 0x00, then a 16-bit BMP whose RGB565 pixels start 54 bytes into the BMP
    /// (rows top to bottom), then a unix time.
    /// Web Hub's convertToBMP writes the pixels 54 bytes into the *package* instead, 2 bytes early, so the
    /// keyboard shows its images shifted one pixel left with the next row's first pixel in the last column.
    /// </summary>
    public static byte[] ImagePackage(ReadOnlySpan<byte> rgba, int w, int h)
    {
        int rowBytes = w * 2, pad = (4 - rowBytes % 4) % 4, stride = rowBytes + pad;
        int pixelBytes = stride * h, dataOffset = 54, total = TypeTagSize + dataOffset + pixelBytes + 4;
        var b = new byte[total];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(s[0..], 72);
        BinaryPrimitives.WriteUInt16LittleEndian(s[1..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], 19778); // "BM"
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], w);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], h);
        BinaryPrimitives.WriteUInt16LittleEndian(s[28..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[30..], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], (uint)pixelBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], 2835);
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 2835);
        BinaryPrimitives.WriteUInt32LittleEndian(s[48..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(s[52..], 0);

        int o = TypeTagSize + dataOffset; // where the keyboard reads the first pixel
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int r = rgba[i], g = rgba[i + 1], bl = rgba[i + 2];
                ushort px = (ushort)(((r >> 3) & 31) << 11 | ((g >> 2) & 63) << 5 | ((bl >> 3) & 31));
                BinaryPrimitives.WriteUInt16LittleEndian(s[o..], px);
                o += 2;
            }
            o += pad;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(s[(total - 4)..], (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return b;
    }
}
