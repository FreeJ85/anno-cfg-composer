using System.Buffers.Binary;
using System.Windows;
using System.Windows.Media.Media3D;

namespace AnnoCfgComposer.Rdm;

/// <summary>
/// .rdm mesh (layout verified against 245 retail 117 mesh rdms, 2026-10-19).
///
/// Arrays are [uint32 count][uint32 element size][data]; pointers point to the start of the data.
///   0x10        → header (0x30)
///   header+4    → mesh info (0x5C)
///   mesh+4      → vertex format header → [0] element descriptor array (16 bytes each: semantic, type, ?, count)
///   mesh+12     → vertex buffer (element size = vertex size 24/28/32)
///   mesh+16     → index buffer (element size 2 = ushort, 4 = uint)
///   mesh+20     → submeshes (28 bytes: start index, index count, material index …)
///
/// Vertex element semantics: 0 position, 1 normal, 2 tangent, 3 binormal, 4 UV, 6 bone weights, 7 color/bone indices
/// Vertex element types: 6 = half × count (P4h → 6,_,4), 5 = byte4 × count (N4b → 5,_,1),
///                 7 = float × count (city wall rdm: P 7,_,3 · T 7,_,2, verified with 44-byte vertices)
/// Animation rdms (anim/) have no mesh and fail here — expected.
/// </summary>
public sealed class RdmMesh
{
    public readonly record struct Submesh(int Start, int Count, int Material);

    public required Point3D[] Positions { get; init; }
    public Vector3D[]? Normals { get; init; }
    public Point[]? Uvs { get; init; }
    public required int[] Indices { get; init; }
    public required Submesh[] Submeshes { get; init; }

    public int TriangleCount => Indices.Length / 3;

    public static RdmMesh Parse(byte[] b)
    {
        if (b.Length < 0x20 || b[0] != 'R' || b[1] != 'D' || b[2] != 'M')
            throw new InvalidDataException(Loc.S("err.rdmHeader"));

        int U(int o)
        {
            if (o < 0 || o + 4 > b.Length) throw new InvalidDataException(Loc.F("err.range", o));
            return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)));
        }

        int header = U(0x10);
        int mesh = U(header + 4);
        int vf = U(U(mesh + 4));
        int elemCount = U(vf - 8);
        int vb = U(mesh + 12);
        int vCount = U(vb - 8), stride = U(vb - 4);
        int ib = U(mesh + 16);
        int iCount = U(ib - 8), iSize = U(ib - 4);
        int sm = U(mesh + 20);
        int smCount = U(sm - 8), smSize = U(sm - 4);

        if (vCount <= 0 || stride <= 0 || (long)vb + (long)vCount * stride > b.Length) throw new InvalidDataException(Loc.S("err.vb"));
        if ((iSize != 2 && iSize != 4) || (long)ib + (long)iCount * iSize > b.Length) throw new InvalidDataException(Loc.S("err.ib"));
        if (elemCount is <= 0 or > 32) throw new InvalidDataException(Loc.S("err.vf"));

        int posOff = -1, posType = 0, nOff = -1, uvOff = -1, uvType = 0, off = 0;
        for (int e = 0; e < elemCount; e++)
        {
            int sem = U(vf + 16 * e), type = U(vf + 16 * e + 4), count = U(vf + 16 * e + 12);
            int size = type switch
            {
                5 => 4 * count,   // byte4
                6 => 2 * count,   // half
                7 => 4 * count,   // float
                _ => throw new InvalidDataException(Loc.F("err.vtype", type, sem)),
            };
            if (sem == 0 && posOff < 0) { posOff = off; posType = type; }
            else if (sem == 1 && nOff < 0 && type == 5) nOff = off;
            else if (sem == 4 && uvOff < 0) { uvOff = off; uvType = type; }
            off += size;
        }
        if (off != stride) throw new InvalidDataException(Loc.F("err.stride", off, stride));
        if (posOff < 0 || posType == 5) throw new InvalidDataException(Loc.S("err.noPos"));

        var pos = new Point3D[vCount];
        var nrm = nOff >= 0 ? new Vector3D[vCount] : null;
        var uv = uvOff >= 0 && uvType != 5 ? new Point[vCount] : null;
        for (int v = 0; v < vCount; v++)
        {
            int at = vb + v * stride;
            pos[v] = new Point3D(Comp(b, at + posOff, posType, 0), Comp(b, at + posOff, posType, 1), Comp(b, at + posOff, posType, 2));
            if (nrm != null)
            {
                int n = at + nOff;
                nrm[v] = new Vector3D(b[n] / 127.5 - 1, b[n + 1] / 127.5 - 1, b[n + 2] / 127.5 - 1);
            }
            if (uv != null) uv[v] = new Point(Comp(b, at + uvOff, uvType, 0), Comp(b, at + uvOff, uvType, 1));
        }

        var idx = new int[iCount];
        for (int i = 0; i < iCount; i++)
        {
            int x = iSize == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(ib + 2 * i)) : U(ib + 4 * i);
            if (x >= vCount) throw new InvalidDataException(Loc.S("err.index"));
            idx[i] = x;
        }

        var subs = new List<Submesh>();
        if (smSize >= 12)
        {
            for (int i = 0; i < smCount; i++)
            {
                int s = U(sm + i * smSize), c = U(sm + i * smSize + 4), m = U(sm + i * smSize + 8);
                if (s >= 0 && c > 0 && (long)s + c <= iCount) subs.Add(new Submesh(s, c - c % 3, m));
            }
        }
        if (subs.Count == 0) subs.Add(new Submesh(0, iCount - iCount % 3, 0));

        return new RdmMesh { Positions = pos, Normals = nrm, Uvs = uv, Indices = idx, Submeshes = [.. subs] };
    }

    static double Comp(byte[] b, int at, int type, int i) => type switch
    {
        6 => (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at + 2 * i))),
        7 => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 4 * i)),
        _ => 0,
    };
}
