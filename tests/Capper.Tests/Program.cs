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
    string promoted = ClipFiles.Promote(pending, final); // keep / save -> finished clip
    Check("Promote returns where the clip landed", promoted == final);
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

    // ---------------------------------------------------------------
    Console.WriteLine("FrameSlotter constant-frame-rate timeline");
    FrameSlotterTest(Check);
}
finally
{
    try { Directory.Delete(tmpRoot, recursive: true); } catch { }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures;

// ===================================================================

static void FrameSlotterTest(Action<string, bool> check)
{
    const long Sec = 10_000_000L;

    var s30 = new FrameSlotter(30, 0);
    check("slot times are exact (slot 30 at 30 fps == 1 s)", s30.SlotTimeHns(30) == Sec);
    long sum = 0;
    for (long k = 0; k < 300; k++) sum += s30.SlotDurationHns(k);
    check("slot durations tile the timeline with no drift", sum == s30.SlotTimeHns(300));
    check("nothing is due before the origin", new FrameSlotter(30, 5 * Sec).SlotsBefore(4 * Sec) == 0);

    // Mirrors WindowCaptureRecorder.EncodeLoop: frames close earlier slots by capture time; returns the
    // frame index shown in each output slot (static stretches are filled at the end).
    static List<int> Simulate(int fps, long[] captureTimes, long endHns)
    {
        var slotter = FrameSlotter.StartingAt(fps, captureTimes[0]);
        var shown = new List<int>();
        int held = 0;
        for (int i = 1; i < captureTimes.Length; i++)
        {
            for (long n = slotter.SlotsBefore(captureTimes[i]); n > 0; n--) { slotter.Take(); shown.Add(held); }
            held = i;
        }
        for (long n = slotter.SlotsBefore(endHns); n > 0; n--) { slotter.Take(); shown.Add(held); }
        return shown;
    }

    // A 60 Hz source with +-1 ms timestamp jitter, recorded at 30 fps: every output frame must be a new
    // frame (no duplicate-then-skip judder), advancing by exactly two source frames each time.
    var rng = new Random(1234);
    long vsync = Sec / 60;
    var t60 = Enumerable.Range(0, 600).Select(i => 1_000 * Sec + i * vsync + rng.Next(-10_000, 10_001)).ToArray();
    var out60 = Simulate(30, t60, t60[^1]);
    // (Slot 0 always shows the first captured frame, so judge steadiness from slot 1 on.)
    var steady = out60.Skip(1).ToList();
    bool steady60 = steady.Zip(steady.Skip(1), (a, b) => b - a).All(d => d == 2);
    check($"60 Hz source -> 30 fps: every slot advances 2 source frames ({out60.Count} slots)", steady60 && out60.Count >= 295);

    // A 30 Hz source (frames on every other vsync) at 30 fps: one-to-one, no repeats or drops.
    var t30 = Enumerable.Range(0, 300).Select(i => 1_000 * Sec + i * 2 * vsync + rng.Next(-10_000, 10_001)).ToArray();
    var out30 = Simulate(30, t30, t30[^1]);
    bool oneToOne30 = out30.Skip(1).Zip(out30.Skip(2), (a, b) => b - a).All(d => d == 1);
    check("30 Hz source -> 30 fps: one-to-one, no repeats or drops", oneToOne30 && out30.Count >= 295);

    // Static window: one frame, then nothing for 2 s -> the held frame fills ~60 slots.
    var outStatic = Simulate(30, new[] { 1_000 * Sec }, 1_002 * Sec);
    check("static window repeats the held frame to fill time", outStatic.Count >= 59 && outStatic.Count <= 61 && outStatic.All(x => x == 0));

    // Playback length tracks capture time regardless of how frames bunch up (e.g. encoder stalls).
    var bunched = new long[] { 0, 1 * Sec, 1 * Sec + 1, 1 * Sec + 2, 3 * Sec };
    var slotter = new FrameSlotter(30, 0);
    long total = 0;
    foreach (var t in bunched.Skip(1)) for (long n = slotter.SlotsBefore(t); n > 0; n--) { slotter.Take(); total++; }
    check("slot count follows capture time, not arrival bunching (3 s -> 90 slots)", total == 90);
}

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
