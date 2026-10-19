using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace AnnoCfgComposer.Render;

/// <summary>
/// .dds → WPF image. All original 117 textures use a DX10 header + BC7 (DXGI 98) and include mipmaps.
/// For large textures only the mip level at or below maxSize is decoded (decoding a full 2048² BC7 is slow).
/// Alpha: normally discarded (building material alpha may hold other data, and WPF 3D does not sort transparency).
/// Only leaf materials (alphaCut given) are cut to 0/255 at the threshold — without semi-transparent values, overlapping leaves break up less.
/// </summary>
public static class DdsTexture
{
    public static BitmapSource? Load(byte[] data, int maxSize, double? alphaCut = null)
    {
        if (data.Length >= 4 && data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ')
            return LoadDds(data, maxSize, alphaCut);
        // Regular images such as .png (mod textures)
        try
        {
            var dec = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource img = dec.Frames[0];
            if (img.PixelWidth > maxSize || img.PixelHeight > maxSize)
            {
                double k = (double)maxSize / Math.Max(img.PixelWidth, img.PixelHeight);
                img = new TransformedBitmap(img, new ScaleTransform(k, k));
            }
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    static BitmapSource? LoadDds(byte[] d, int maxSize, double? alphaCut)
    {
        if (Decode(d, maxSize) is not var (px, w, h)) return null;

        var bgra = new byte[w * h * 4];
        for (int i = 0; i < px.Length && i < w * h; i++)
        {
            bgra[i * 4] = px[i].b;
            bgra[i * 4 + 1] = px[i].g;
            bgra[i * 4 + 2] = px[i].r;
            bgra[i * 4 + 3] = alphaCut is double cut ? (px[i].a >= cut * 255 ? (byte)255 : (byte)0) : (byte)255;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Prepares an alpha-cutout texture for display: an RGBA8 dds with alpha cut to 0/255 at cut, mipmaps included.
    /// null if no pixels would be cut (opaque texture) — just upload the original dds to the GPU.
    /// The Helix shader blends semi-transparent pixels with the background, making grass look blurry (the game cuts hard at 0.5).
    /// </summary>
    public static byte[]? CutoutDds(byte[] d, int maxSize, double cut)
    {
        // First check a small mip for pixels that would be cut (opaque textures skip the big decode)
        if (LowAlphaFraction(d, cut) is <= 0) return null;
        if (Decode(d, maxSize) is not var (px, w, h)) return null;

        byte limit = (byte)Math.Clamp(cut * 255, 1, 255);
        var level = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            bool on = px[i].a >= limit;
            level[i * 4] = px[i].r;
            level[i * 4 + 1] = px[i].g;
            level[i * 4 + 2] = px[i].b;
            level[i * 4 + 3] = on ? (byte)255 : (byte)0;
        }

        var levels = new List<byte[]> { level };
        int lw = w, lh = h;
        while (lw > 1 || lh > 1)
        {
            int nw = Math.Max(1, lw / 2), nh = Math.Max(1, lh / 2);
            var next = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            {
                // Average only visible texel colors (mixing in transparent texel colors darkens leaf edges); visible if at least half are visible
                int r = 0, g = 0, b = 0, on = 0, n = 0;
                for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    int sx = Math.Min(lw - 1, x * 2 + dx), sy = Math.Min(lh - 1, y * 2 + dy);
                    int o = (sy * lw + sx) * 4;
                    n++;
                    if (level[o + 3] == 0) continue;
                    r += level[o]; g += level[o + 1]; b += level[o + 2]; on++;
                }
                int t = (y * nw + x) * 4;
                if (on > 0) { next[t] = (byte)(r / on); next[t + 1] = (byte)(g / on); next[t + 2] = (byte)(b / on); }
                next[t + 3] = on * 2 >= n ? (byte)255 : (byte)0;
            }
            levels.Add(next);
            level = next; lw = nw; lh = nh;
        }

        // dds with DX10 header, DXGI_FORMAT_R8G8B8A8_UNORM(28), all mips
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0x20534444u);                                  // "DDS "
        bw.Write(124u);
        bw.Write(0x1u | 0x2u | 0x4u | 0x8u | 0x1000u | 0x20000u); // CAPS HEIGHT WIDTH PITCH PIXELFORMAT MIPMAPCOUNT
        bw.Write((uint)h);
        bw.Write((uint)w);
        bw.Write((uint)(w * 4));
        bw.Write(0u);
        bw.Write((uint)levels.Count);
        for (int i = 0; i < 11; i++) bw.Write(0u);
        bw.Write(32u); bw.Write(0x4u); bw.Write(0x30315844u);  // pixel format: FOURCC "DX10"
        for (int i = 0; i < 5; i++) bw.Write(0u);
        bw.Write(0x1000u | 0x400000u | 0x8u);                   // TEXTURE MIPMAP COMPLEX
        for (int i = 0; i < 4; i++) bw.Write(0u);
        bw.Write(28u); bw.Write(3u); bw.Write(0u); bw.Write(1u); bw.Write(0u);  // DXGI 28, TEXTURE2D, array size 1
        foreach (var l in levels) bw.Write(l);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Fraction of pixels with alpha below cut (counted on a small mip). -1 if unreadable.</summary>
    public static double LowAlphaFraction(byte[] d, double cut)
    {
        if (Decode(d, 128) is not var (px, w, h) || px.Length == 0) return -1;
        int low = 0;
        foreach (var c in px) if (c.a < cut * 255) low++;
        return (double)low / px.Length;
    }

    static (ColorRgba32[] px, int w, int h)? Decode(byte[] d, int maxSize)
    {
        if (d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S') return null;
        int height = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(12));
        int width = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(16));
        int mips = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(28)));
        string fourCC = System.Text.Encoding.ASCII.GetString(d, 84, 4);
        int dataStart = 128;
        CompressionFormat fmt;
        if (fourCC == "DX10")
        {
            int dxgi = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(128));
            dataStart = 148;
            fmt = dxgi switch
            {
                70 or 71 or 72 => CompressionFormat.Bc1,
                73 or 74 or 75 => CompressionFormat.Bc2,
                76 or 77 or 78 => CompressionFormat.Bc3,
                79 or 80 or 81 => CompressionFormat.Bc4,
                82 or 83 or 84 => CompressionFormat.Bc5,
                97 or 98 or 99 => CompressionFormat.Bc7,
                _ => CompressionFormat.Unknown,
            };
        }
        else
        {
            fmt = fourCC switch
            {
                "DXT1" => CompressionFormat.Bc1,
                "DXT3" => CompressionFormat.Bc2,
                "DXT5" => CompressionFormat.Bc3,
                "ATI1" or "BC4U" => CompressionFormat.Bc4,
                "ATI2" or "BC5U" => CompressionFormat.Bc5,
                _ => CompressionFormat.Unknown,
            };
        }
        if (fmt == CompressionFormat.Unknown || width <= 0 || height <= 0) return null;
        int blockBytes = fmt is CompressionFormat.Bc1 or CompressionFormat.Bc4 ? 8 : 16;

        long offset = dataStart;
        int w = width, h = height;
        for (int level = 0; level + 1 < mips && (w > maxSize || h > maxSize) && w > 4 && h > 4; level++)
        {
            offset += MipBytes(w, h, blockBytes);
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        long bytes = MipBytes(w, h, blockBytes);
        if (offset + bytes > d.Length) return null;

        var slice = new byte[bytes];
        Buffer.BlockCopy(d, (int)offset, slice, 0, (int)bytes);
        return (new BcDecoder().DecodeRaw(slice, w, h, fmt), w, h);
    }

    static long MipBytes(int w, int h, int blockBytes) => (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes;
}
