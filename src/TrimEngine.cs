using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Capper;

/// <summary>Trim/re-encode clips using the native Windows.Media.Editing pipeline.</summary>
internal static class TrimEngine
{
    /// <summary>
    /// Trim [start, end] from <paramref name="inputPath"/> and re-encode to <paramref name="outputPath"/>
    /// at the given bitrates, preserving the source resolution. Throws if the render fails.
    /// </summary>
    public static async Task TrimAsync(
        string inputPath, string outputPath, TimeSpan start, TimeSpan end,
        int videoBitrateBps, int audioBitrateBps, IProgress<double>? progress,
        CancellationToken cancel = default)
    {
        var inFile = await StorageFile.GetFileFromPathAsync(inputPath);
        var clip = await MediaClip.CreateFromFileAsync(inFile);
        var total = clip.OriginalDuration;

        if (start < TimeSpan.Zero) start = TimeSpan.Zero;
        if (end > total) end = total;
        clip.TrimTimeFromStart = start;
        clip.TrimTimeFromEnd = total - end >= TimeSpan.Zero ? total - end : TimeSpan.Zero;

        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        var vp = clip.GetVideoEncodingProperties();
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = vp.Width;
        profile.Video.Height = vp.Height;
        profile.Video.Bitrate = (uint)Math.Max(100_000, videoBitrateBps);
        if (profile.Audio != null && audioBitrateBps > 0)
            profile.Audio.Bitrate = (uint)audioBitrateBps;

        var dir = Path.GetDirectoryName(outputPath)!;
        var folder = await StorageFolder.GetFolderFromPathAsync(dir);
        var outFile = await folder.CreateFileAsync(Path.GetFileName(outputPath), CreationCollisionOption.ReplaceExisting);

        var op = composition.RenderToFileAsync(outFile, MediaTrimmingPreference.Precise, profile);
        op.Progress = (_, p) => progress?.Report(p);
        using (cancel.Register(() => { try { op.Cancel(); } catch { } }))
        {
            var reason = await op; // throws OperationCanceledException if op.Cancel() ran
            if (reason != TranscodeFailureReason.None)
                throw new InvalidOperationException($"Trim failed: {reason}");
        }
    }
}
