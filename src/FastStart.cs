using System.IO;

namespace Capper;

/// <summary>
/// Relocates an MP4's <c>moov</c> atom ahead of <c>mdat</c> ("fast-start"), so players can begin
/// playback before the whole file downloads. Both Media Foundation (recording) and
/// Windows.Media.Editing (trim) write <c>moov</c> at the end; this rewrites the file in place.
///
/// Moving <c>moov</c> in front of <c>mdat</c> shifts the media data forward by the size of the
/// <c>moov</c> box, so every chunk offset in the sample tables (<c>stco</c>/<c>co64</c>) is bumped
/// by that delta. No external tools required.
/// </summary>
internal static class FastStart
{
    /// <summary>Best-effort: returns true if the file is now fast-start (or already was).
    /// On any failure the original file is left untouched and false is returned.</summary>
    public static bool Process(string path)
    {
        string tmp = path + ".fs.tmp";
        try
        {
            using (var fs = File.OpenRead(path))
            {
                var boxes = ReadTopLevelBoxes(fs);
                Box? moov = Find(boxes, "moov");
                Box? mdat = Find(boxes, "mdat");
                if (moov == null || mdat == null) return false;     // nothing to do / not an mp4 we handle
                if (moov.Start < mdat.Start) return true;           // already fast-start

                // Read and patch the moov box; mdat moves forward by exactly moov's size.
                var moovBytes = new byte[moov.Size];
                fs.Position = moov.Start;
                ReadFully(fs, moovBytes, 0, moovBytes.Length);
                PatchChunkOffsets(moovBytes, 0, moovBytes.Length, moov.Size);

                using var outFs = File.Create(tmp);
                // Leading boxes (everything before mdat: ftyp, uuid, …) in original order.
                foreach (var b in boxes)
                {
                    if (b.Start >= mdat.Start || b.Type == "moov") continue;
                    CopyRange(fs, outFs, b.Start, b.Size);
                }
                outFs.Write(moovBytes, 0, moovBytes.Length);        // moov now precedes mdat
                CopyRange(fs, outFs, mdat.Start, mdat.Size);        // the media data
                // Any trailing boxes that sat after mdat (other than moov), preserved in order.
                foreach (var b in boxes)
                {
                    if (b.Start <= mdat.Start || b.Type == "moov") continue;
                    CopyRange(fs, outFs, b.Start, b.Size);
                }
            }

            // Swap the rewritten file in.
            string bak = path + ".fs.bak";
            if (File.Exists(bak)) File.Delete(bak);
            File.Replace(tmp, path, bak);
            File.Delete(bak);
            return true;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    private sealed record Box(string Type, long Start, long Size);

    private static Box? Find(List<Box> boxes, string type) => boxes.Find(b => b.Type == type);

    private static List<Box> ReadTopLevelBoxes(Stream s)
    {
        var list = new List<Box>();
        long len = s.Length;
        s.Position = 0;
        var hdr = new byte[16];
        while (s.Position + 8 <= len)
        {
            long start = s.Position;
            ReadFully(s, hdr, 0, 8);
            long size = ReadU32(hdr, 0);
            string type = System.Text.Encoding.ASCII.GetString(hdr, 4, 4);
            if (size == 1)
            {
                ReadFully(s, hdr, 8, 8);
                size = ReadU64(hdr, 8);
            }
            else if (size == 0)
            {
                size = len - start; // box extends to end of file
            }
            if (size < 8 || start + size > len) break; // malformed
            list.Add(new Box(type, start, size));
            s.Position = start + size;
        }
        return list;
    }

    /// <summary>Recursively bump every stco/co64 chunk offset within a moov segment by <paramref name="delta"/>.</summary>
    private static void PatchChunkOffsets(byte[] buf, int offset, int end, long delta)
    {
        int p = offset;
        while (p + 8 <= end)
        {
            long size = ReadU32(buf, p);
            string type = System.Text.Encoding.ASCII.GetString(buf, p + 4, 4);
            int header = 8;
            if (size == 1) { size = ReadU64(buf, p + 8); header = 16; }
            if (size < header || p + size > end) break;

            int content = p + header;
            switch (type)
            {
                case "moov": case "trak": case "mdia": case "minf": case "stbl":
                    PatchChunkOffsets(buf, content, (int)(p + size), delta); // container box
                    break;
                case "stco": // 32-bit chunk offsets
                {
                    long count = ReadU32(buf, content + 4); // after version+flags
                    int e = content + 8;
                    for (long i = 0; i < count && e + 4 <= p + size; i++, e += 4)
                        WriteU32(buf, e, (uint)(ReadU32(buf, e) + delta));
                    break;
                }
                case "co64": // 64-bit chunk offsets
                {
                    long count = ReadU32(buf, content + 4);
                    int e = content + 8;
                    for (long i = 0; i < count && e + 8 <= p + size; i++, e += 8)
                        WriteU64(buf, e, (ulong)(ReadU64(buf, e) + delta));
                    break;
                }
            }
            p += (int)size;
        }
    }

    // --- big-endian helpers ---
    private static long ReadU32(byte[] b, int o) =>
        ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];

    private static long ReadU64(byte[] b, int o)
    {
        long v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i];
        return v;
    }

    private static void WriteU32(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static void WriteU64(byte[] b, int o, ulong v)
    {
        for (int i = 7; i >= 0; i--) { b[o + i] = (byte)v; v >>= 8; }
    }

    private static void ReadFully(Stream s, byte[] buf, int offset, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, offset + read, count - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private static void CopyRange(Stream src, Stream dst, long start, long size)
    {
        src.Position = start;
        var buf = new byte[81920];
        long remaining = size;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buf.Length, remaining);
            int n = src.Read(buf, 0, want);
            if (n <= 0) throw new EndOfStreamException();
            dst.Write(buf, 0, n);
            remaining -= n;
        }
    }
}
