using System.Collections.Specialized;
using System.IO;
using System.Windows.Forms;

namespace Capper;

/// <summary>Puts a clip file on the clipboard so it can be pasted into Discord, chat apps or Explorer.</summary>
internal static class ClipClipboard
{
    /// <summary>
    /// Copy <paramref name="path"/> to the clipboard the same way Explorer's Copy does (file drop list +
    /// "Preferred DropEffect" = copy), which is what Discord attaches on Ctrl+V. Must run on an STA
    /// (UI) thread. Throws if the clipboard stays locked by another app.
    /// </summary>
    public static void CopyFile(string path)
    {
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { path });
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 100); // may be briefly locked
    }

    /// <summary>"Capper-….mp4 (4.4 MB)" for confirmation messages.</summary>
    public static string Describe(string path)
    {
        double mb = 0;
        try { mb = new FileInfo(path).Length / (1024.0 * 1024.0); } catch { }
        return $"{Path.GetFileName(path)} ({mb:0.0} MB)";
    }
}
