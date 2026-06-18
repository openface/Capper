using System.IO;
using System.Threading;

namespace Capper;

/// <summary>
/// Naming + promotion rules for a clip's lifecycle in the output folder. A recording's state is
/// encoded in its filename so an in-progress file is never mistaken for a finished clip:
/// <list type="bullet">
/// <item><c>Capper-&lt;date&gt;.pending.mp4</c> — recording / awaiting a trim decision</item>
/// <item><c>Capper-&lt;date&gt;.trimming.mp4</c> — being re-encoded during Save</item>
/// <item><c>Capper-&lt;date&gt;.mp4</c> — finished clip</item>
/// </list>
/// Pure file/path logic, kept free of UI/capture dependencies so it can be unit-tested.
/// </summary>
internal static class ClipFiles
{
    public const string PendingSuffix = ".pending";
    public const string TrimmingSuffix = ".trimming";

    /// <summary>The in-progress recording's path for a given finished-clip path.</summary>
    public static string PendingPath(string finalPath) => SuffixedSibling(finalPath, PendingSuffix);

    /// <summary>The temporary trim-output path for a given finished-clip path.</summary>
    public static string TrimmingPath(string finalPath) => SuffixedSibling(finalPath, TrimmingSuffix);

    private static string SuffixedSibling(string finalPath, string suffix) =>
        Path.Combine(Path.GetDirectoryName(finalPath)!,
                     Path.GetFileNameWithoutExtension(finalPath) + suffix + Path.GetExtension(finalPath));

    /// <summary>Move <paramref name="from"/> onto <paramref name="to"/>, retrying briefly while the
    /// source may still be releasing handles. Falls back to a "_1" sibling if the target is locked.</summary>
    public static void Promote(string from, string to)
    {
        for (int i = 0; i < 12; i++)
        {
            try
            {
                if (File.Exists(to)) File.Delete(to);
                File.Move(from, to);
                return;
            }
            catch (IOException) { Thread.Sleep(150); }
        }
        string alt = Path.Combine(Path.GetDirectoryName(to)!, Path.GetFileNameWithoutExtension(to) + "_1" + Path.GetExtension(to));
        if (File.Exists(from)) File.Move(from, alt, overwrite: true);
    }

    /// <summary>Delete a clip file, retrying briefly while a handle may still be releasing.</summary>
    public static void Discard(string path)
    {
        for (int i = 0; i < 12; i++)
        {
            try { if (File.Exists(path)) File.Delete(path); return; }
            catch (IOException) { Thread.Sleep(120); }
            catch { return; }
        }
    }
}
