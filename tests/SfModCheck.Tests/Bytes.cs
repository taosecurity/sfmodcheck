// Byte helpers for breaking a written plugin on purpose (the same ones the private SelfTest uses).
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SfModCheck.Tests;

public record Item(long Off, string Sig, bool Group, int GroupType, List<long> Parents);

public static class Bytes
{
    public static uint U32(byte[] b, long o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)o));
    public static void SetU32(byte[] b, long o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)o), v);
    public static void SetU16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
    public static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));

    /// <summary>NVMI bytes after the edge, preferred-edge and door lists, as Mutagen 0.54.4 keeps them
    /// (NavigationMapInfo.Unknown): one 60-byte traversal (From/To navmesh at 52/56, stored FormIDs), no island data,
    /// an empty pathing cell.</summary>
    public static byte[] Traversal(uint from, uint to)
    {
        var b = new byte[4 + 60 + 1 + 17];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 + 52), from);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 + 56), to);
        return b;
    }

    /// <summary>Every group and record with its offset and the offsets of the groups around it.</summary>
    public static List<Item> Items(byte[] b)
    {
        var list = new List<Item>();
        void Walk(long start, long end, List<long> parents)
        {
            long off = start;
            while (off + 24 <= end)
            {
                var sig = Encoding.ASCII.GetString(b, (int)off, 4);
                var size = U32(b, off + 4);
                if (sig == "GRUP")
                {
                    list.Add(new Item(off, sig, true, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan((int)off + 12)), parents.ToList()));
                    Walk(off + 24, off + size, parents.Append(off).ToList());
                    off += size;
                }
                else
                {
                    list.Add(new Item(off, sig, false, -1, parents.ToList()));
                    off += 24 + size;
                }
            }
        }
        Walk(24 + U32(b, 4), b.Length, new());
        return list;
    }

    /// <summary>Rebuilds the TES4 header from an edited field list (sizes fixed).</summary>
    public static byte[] RewriteHeader(byte[] b, Action<List<(string Type, byte[] Data)>> edit)
    {
        var size = (int)U32(b, 4);
        var fields = Subrecords.Parse(b.AsSpan(24, size), out _);
        edit(fields);
        var body = new MemoryStream();
        foreach (var (t, d) in fields)
        {
            body.Write(Encoding.ASCII.GetBytes(t));
            body.Write(BitConverter.GetBytes((ushort)d.Length));
            body.Write(d);
        }
        var head = b[..24];
        SetU32(head, 4, (uint)body.Length);
        return head.Concat(body.ToArray()).Concat(b[(24 + size)..]).ToArray();
    }

    /// <summary>Stores a record compressed (zlib, with the uncompressed size first, + sizeDelta to break it); group
    /// sizes fixed.</summary>
    public static byte[] Compress(byte[] b, Item rec, int sizeDelta)
    {
        var size = (int)U32(b, rec.Off + 4);
        var body = b.AsSpan((int)rec.Off + 24, size).ToArray();
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(body);
        var stored = BitConverter.GetBytes((uint)(body.Length + sizeDelta)).Concat(z.ToArray()).ToArray();
        var head = b.AsSpan((int)rec.Off, 24).ToArray();
        SetU32(head, 4, (uint)stored.Length);
        SetU32(head, 8, U32(head, 8) | Flags.Compressed);
        var delta = stored.Length - size;
        var outB = b[..(int)rec.Off].Concat(head).Concat(stored).Concat(b[((int)rec.Off + 24 + size)..]).ToArray();
        foreach (var g in rec.Parents) SetU32(outB, g + 4, (uint)(U32(outB, g + 4) + delta));
        return outB;
    }
}
