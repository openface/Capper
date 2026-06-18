using System.Text;
using Capper;

// Minimal dependency-free test runner: prints PASS/FAIL per check and exits non-zero on any failure.
int failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
    if (!ok) failures++;
}

// Optional ad-hoc mode: `dotnet run -- <real.mp4>` fast-starts a copy of a real clip and reports.
if (args.Length > 0)
{
    string src = args[0];
    string copy = Path.Combine(Path.GetTempPath(), "fs-real-" + Path.GetFileName(src));
    File.Copy(src, copy, overwrite: true);
    var pre = File.ReadAllBytes(copy);
    Console.WriteLine($"before: moov@{IndexOf(pre, "moov")} mdat@{IndexOf(pre, "mdat")}");
    bool fok = FastStart.Process(copy);
    var post = File.ReadAllBytes(copy);
    Console.WriteLine($"after : moov@{IndexOf(post, "moov")} mdat@{IndexOf(post, "mdat")}  (Process={fok})");
    Check("real clip: Process returned true", fok);
    Check("real clip: moov now precedes mdat", IndexOf(post, "moov") < IndexOf(post, "mdat"));
    Check("real clip: byte length unchanged", post.Length == pre.Length);
    Console.WriteLine("kept: " + copy); // left on disk so the caller can validate it decodes
    return failures;
}

string tmpRoot = Path.Combine(Path.GetTempPath(), "capper-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmpRoot);

try
{
    // ---------------------------------------------------------------
    Console.WriteLine("ClipFiles naming");
    string final = Path.Combine(tmpRoot, "Capper-20260615-101216.mp4");
    Check("PendingPath adds .pending before extension",
        ClipFiles.PendingPath(final) == Path.Combine(tmpRoot, "Capper-20260615-101216.pending.mp4"));
    Check("TrimmingPath adds .trimming before extension",
        ClipFiles.TrimmingPath(final) == Path.Combine(tmpRoot, "Capper-20260615-101216.trimming.mp4"));
    Check("Finished name has no state suffix",
        !Path.GetFileNameWithoutExtension(final).Contains('.'));

    // ---------------------------------------------------------------
    Console.WriteLine("ClipFiles lifecycle (promote / discard)");
    string pending = ClipFiles.PendingPath(final);
    File.WriteAllText(pending, "RECORDED");
    ClipFiles.Promote(pending, final); // keep / save -> finished clip
    Check("Promote creates the finished clip", File.Exists(final));
    Check("Promote removes the pending file", !File.Exists(pending));
    Check("Promote preserves contents", File.ReadAllText(final) == "RECORDED");

    // Promote overwrites an existing finished clip (re-save case).
    string pending2 = ClipFiles.PendingPath(final);
    File.WriteAllText(pending2, "NEWER");
    ClipFiles.Promote(pending2, final);
    Check("Promote overwrites an existing finished clip", File.ReadAllText(final) == "NEWER");

    ClipFiles.Discard(final);
    Check("Discard deletes the clip", !File.Exists(final));
    ClipFiles.Discard(final); // discarding a missing file is a no-op
    Check("Discard on a missing file is a no-op (no throw)", true);

    // ---------------------------------------------------------------
    Console.WriteLine("AppConfig capture mode");
    Check("CaptureMode defaults to ActiveWindow", new AppConfig().CaptureMode == CaptureMode.ActiveWindow);
    var cloned = new AppConfig { CaptureMode = CaptureMode.FullScreen }.Clone();
    Check("Clone preserves CaptureMode", cloned.CaptureMode == CaptureMode.FullScreen);
    var json = System.Text.Json.JsonSerializer.Serialize(new AppConfig { CaptureMode = CaptureMode.FullScreen });
    Check("CaptureMode serializes as a string (not an int)", json.Contains("\"FullScreen\""));
    var back = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json)!;
    Check("CaptureMode round-trips through JSON", back.CaptureMode == CaptureMode.FullScreen);

    // ---------------------------------------------------------------
    Console.WriteLine("AppConfig video presets");
    Check("QuickShare preset resolves to 720p/30/2500", AppConfig.PresetValues(VideoPreset.QuickShare) == (720, 30, 2500));
    Check("Original keeps source resolution (height 0)", AppConfig.PresetValues(VideoPreset.Original).Height == 0);
    var cfgP = new AppConfig();
    cfgP.ApplyPreset(VideoPreset.LongClip);
    Check("ApplyPreset sets height/fps/bitrate together",
        cfgP is { Preset: VideoPreset.LongClip, TargetHeight: 480, Fps: 30, VideoBitrateKbps: 1200 });
    Check("Clone preserves preset + target height",
        cfgP.Clone() is { Preset: VideoPreset.LongClip, TargetHeight: 480 });
    var pj = System.Text.Json.JsonSerializer.Serialize(cfgP);
    Check("Preset serializes as its name", pj.Contains("\"LongClip\""));
    Check("Preset round-trips through JSON",
        System.Text.Json.JsonSerializer.Deserialize<AppConfig>(pj)!.Preset == VideoPreset.LongClip);

    // Legacy config.json (pre-rename) must migrate to the new preset, not reset to default.
    string legacy = "{\"Preset\":\"Discord\"}";
    Check("legacy \"Discord\" migrates to QuickShare",
        System.Text.Json.JsonSerializer.Deserialize<AppConfig>(legacy)!.Preset == VideoPreset.QuickShare);
    Check("legacy \"OriginalQuality\" migrates to Original",
        System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"Preset\":\"OriginalQuality\"}")!.Preset == VideoPreset.Original);
    Check("legacy \"Sharp\" migrates to Tutorial",
        System.Text.Json.JsonSerializer.Deserialize<AppConfig>("{\"Preset\":\"Sharp\"}")!.Preset == VideoPreset.Tutorial);

    // ---------------------------------------------------------------
    Console.WriteLine("FastStart relocates moov ahead of mdat and fixes chunk offsets");
    FastStartTest(Check, tmpRoot);
}
finally
{
    try { Directory.Delete(tmpRoot, recursive: true); } catch { }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures;

// ===================================================================

static void FastStartTest(Action<string, bool> check, string dir)
{
    // Build a structurally valid, moov-at-end MP4 with a single stco entry pointing at a known
    // marker inside mdat, then verify FastStart moves moov to the front and bumps the offset so it
    // still lands on the marker.
    byte[] U32(long v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        long size = 8 + body.Length;
        var b = new byte[size];
        Array.Copy(U32(size), 0, b, 0, 4);
        Array.Copy(Encoding.ASCII.GetBytes(type), 0, b, 4, 4);
        Array.Copy(body, 0, b, 8, body.Length);
        return b;
    }

    var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(0x200), Encoding.ASCII.GetBytes("isom"));
    long mdatStart = ftyp.Length;

    byte[] marker = Encoding.ASCII.GetBytes("CHNKMRK!"); // 8 bytes, never collides with box types
    int markerPosInPayload = 4;
    var payload = new byte[64];
    Array.Copy(marker, 0, payload, markerPosInPayload, marker.Length);
    long chunkAbs = mdatStart + 8 + markerPosInPayload; // absolute file offset of the marker

    var mdat = Box("mdat", payload);
    var stco = Box("stco", U32(0) /*version+flags*/, U32(1) /*count*/, U32(chunkAbs));
    var moov = Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", stco)))));

    string path = Path.Combine(dir, "synthetic.mp4");
    using (var fs = File.Create(path))
    {
        fs.Write(ftyp); fs.Write(mdat); fs.Write(moov); // moov LAST (not fast-start)
    }

    // Sanity: it really starts non-fast-start.
    var before = File.ReadAllBytes(path);
    check("synthetic clip has moov after mdat", IndexOf(before, "mdat") < IndexOf(before, "moov"));

    bool ok = FastStart.Process(path);
    check("FastStart.Process returns true", ok);

    var after = File.ReadAllBytes(path);
    check("moov now precedes mdat", IndexOf(after, "moov") < IndexOf(after, "mdat"));

    long newOffset = ReadStcoEntry(after);
    check("stco offset bumped by moov size", newOffset == chunkAbs + moov.Length);

    // The bumped offset must still land exactly on the marker bytes.
    bool landsOnMarker = newOffset + marker.Length <= after.Length
        && after.Skip((int)newOffset).Take(marker.Length).SequenceEqual(marker);
    check("bumped offset still points at the media chunk", landsOnMarker);

    // Idempotent: a second pass is a no-op that keeps it valid.
    bool ok2 = FastStart.Process(path);
    check("FastStart is idempotent (already fast-start)", ok2 && ReadStcoEntry(File.ReadAllBytes(path)) == newOffset);
}

static int IndexOf(byte[] hay, string needle)
{
    var n = Encoding.ASCII.GetBytes(needle);
    for (int i = 0; i + n.Length <= hay.Length; i++)
    {
        bool m = true;
        for (int j = 0; j < n.Length; j++) if (hay[i + j] != n[j]) { m = false; break; }
        if (m) return i;
    }
    return -1;
}

// Box layout: [size:4]["stco"]["version+flags":4]["count":4]["offset":4]
static long ReadStcoEntry(byte[] file)
{
    int i = IndexOf(file, "stco");
    int o = i + 4 + 4 + 4; // skip type, version/flags, count -> first entry
    return ((long)file[o] << 24) | ((long)file[o + 1] << 16) | ((long)file[o + 2] << 8) | file[o + 3];
}
