namespace Capper;

/// <summary>
/// Maps captured frames, which arrive at irregular times (Windows.Graphics.Capture only delivers a
/// frame when the window actually changes), onto a constant-frame-rate output timeline.
///
/// Output slot <c>k</c> has presentation time <c>k / fps</c> after <see cref="OriginHns"/> and shows
/// the most recent frame captured at or before that instant (sample-and-hold). Slot assignment is
/// driven by each frame's <b>capture</b> timestamp, never by when the encode thread happens to wake
/// up, so OS timer granularity can't cause duplicated-then-skipped frames (visible judder).
///
/// Pure arithmetic, no capture/encode dependencies, so it is unit-tested directly.
/// </summary>
public sealed class FrameSlotter
{
    private const long HnsPerSecond = 10_000_000L;
    private readonly int _fps;

    /// <param name="fps">Output frame rate.</param>
    /// <param name="originHns">Timeline zero, in the same clock as the capture timestamps (100 ns units).</param>
    public FrameSlotter(int fps, long originHns)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        _fps = fps;
        OriginHns = originHns;
    }

    /// <summary>
    /// A slotter whose grid starts a quarter slot before the first captured frame. Captured frames are
    /// vsync-aligned, so slot boundaries should sit <i>between</i> vsyncs, where small timestamp jitter
    /// can't flip a frame into the neighbouring slot. At 30 fps a quarter slot is 8.3 ms, i.e. midway
    /// between 60 Hz vsyncs, for both 30 Hz and 60 Hz sources. (Half a slot would land every boundary
    /// exactly on a 60 Hz vsync, the worst case.)
    /// </summary>
    public static FrameSlotter StartingAt(int fps, long firstFrameHns) =>
        new(fps, firstFrameHns - HnsPerSecond / fps / 4);

    public long OriginHns { get; }

    /// <summary>Index of the next output slot that has not been emitted yet.</summary>
    public long NextSlot { get; private set; }

    /// <summary>Presentation time of slot <paramref name="slot"/> relative to the origin. Computed
    /// from the slot index (not by accumulating a rounded duration), so it never drifts.</summary>
    public long SlotTimeHns(long slot) => slot * HnsPerSecond / _fps;

    /// <summary>Duration of slot <paramref name="slot"/> (33.33 ms at 30 fps, rounded so consecutive
    /// slots tile the timeline exactly).</summary>
    public long SlotDurationHns(long slot) => SlotTimeHns(slot + 1) - SlotTimeHns(slot);

    /// <summary>
    /// How many not-yet-emitted slots have a presentation time strictly before <paramref name="timeHns"/>.
    /// Call it with a new frame's capture time: those slots are now final and should show the
    /// <i>previous</i> frame. Call it with "now minus capture latency" to close slots while the window is
    /// static and no frames arrive.
    /// </summary>
    public long SlotsBefore(long timeHns)
    {
        long rel = timeHns - OriginHns;
        if (rel <= 0) return 0;
        // First slot whose time is >= rel: ceil(rel * fps / 1e7). Every slot before it is in the past.
        long firstNotBefore = (rel * _fps + HnsPerSecond - 1) / HnsPerSecond;
        return Math.Max(0, firstNotBefore - NextSlot);
    }

    /// <summary>Mark the current <see cref="NextSlot"/> as emitted and return its index.</summary>
    public long Take() => NextSlot++;
}
