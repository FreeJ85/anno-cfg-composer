using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AnnoCfgComposer.Game;

/// <summary>One file inside an archive.</summary>
public sealed class RdaEntry
{
    public required string Path { get; init; }
    public required RdaArchive Archive { get; init; }
    public long Offset { get; init; }
    public long CompressedSize { get; init; }
    public long Size { get; init; }
    public bool Compressed { get; init; }
}

/// <summary>
/// RDA v2.2 reader (same rules as reference/rda-extract.js).
///
/// Blocks are laid out as [data][directory][32-byte header]; the uint64 at the end of the header points to the next block header.
/// The first block offset is stored at file offset 0x310. For patch archives (zz_patchfiles_*) the first block is at 0x318.
///
/// Block header, 32 bytes:
///   +0  uint32 flags  (1 = zlib compressed, 2 = encrypted, 4 = memory resident, 8 = deletion marker)
///   +4  uint32 fileCount
///   +8  uint64 compressed directory size (directory sits right before the header)
///   +16 uint64 uncompressed directory size (fileCount * 560)
///   +24 uint64 offset of the next block header
///
/// flags 8 blocks list files deleted by a patch (size 0). They remove the same paths from earlier archives.
/// 117 retail has no 1/2/4 blocks, but 1 can be read, and 2/4 are skipped.
/// </summary>
public sealed class RdaArchive : IDisposable
{
    const int EntrySize = 560;
    readonly SafeFileHandle handle;

    public string FilePath { get; }
    public string Name => System.IO.Path.GetFileName(FilePath);

    RdaArchive(string path)
    {
        FilePath = path;
        // Readable even while the game or Steam has the archive open (with only FileShare.Read, graphics_roman.rda fails with "in use by another process")
        handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    public static RdaArchive Open(string path, List<RdaEntry> files, List<string> deleted)
    {
        var a = new RdaArchive(path);
        try
        {
            long size = RandomAccess.GetLength(a.handle);
            long off = (long)BinaryPrimitives.ReadUInt64LittleEndian(a.ReadBytes(0x310, 8));
            int guard = 0;
            while (off >= 0x318 && off + 32 <= size && guard++ < 1_000_000)
            {
                byte[] h = a.ReadBytes(off, 32);
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0));
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
                long csize = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(8));
                long next = (long)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(24));

                if (count > 0 && csize > 0 && csize < 200_000_000 && csize <= off && (flags & (2 | 4)) == 0)
                {
                    byte[] dir = a.ReadBytes(off - csize, (int)csize);
                    if ((flags & 1) != 0) dir = Inflate(dir);
                    for (int i = 0; i < count && (i + 1) * EntrySize <= dir.Length; i++)
                    {
                        var e = dir.AsSpan(i * EntrySize, EntrySize);
                        string name = Encoding.Unicode.GetString(e[..520]);
                        int z = name.IndexOf('\0');
                        if (z >= 0) name = name[..z];
                        if (name.Length == 0) continue;
                        if ((flags & 8) != 0) { deleted.Add(name); continue; }
                        files.Add(new RdaEntry
                        {
                            Path = name,
                            Archive = a,
                            Offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[520..]),
                            CompressedSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[528..]),
                            Size = (long)BinaryPrimitives.ReadUInt64LittleEndian(e[536..]),
                            Compressed = (flags & 1) != 0,
                        });
                    }
                }
                if (next == off || next >= size) break;
                off = next;
            }
            return a;
        }
        catch
        {
            a.Dispose();
            throw;
        }
    }

    public byte[] Read(RdaEntry e)
    {
        byte[] data = ReadBytes(e.Offset, checked((int)e.CompressedSize));
        return e.Compressed ? Inflate(data) : data;
    }

    byte[] ReadBytes(long offset, int length)
    {
        var buf = new byte[length];
        int done = 0;
        while (done < length)
        {
            int n = RandomAccess.Read(handle, buf.AsSpan(done), offset + done);
            if (n <= 0) throw new EndOfStreamException($"{Name} @{offset + done}");
            done += n;
        }
        return buf;
    }

    static byte[] Inflate(byte[] data)
    {
        using var src = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var dst = new MemoryStream();
        src.CopyTo(dst);
        return dst.ToArray();
    }

    public void Dispose() => handle.Dispose();
}
