using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Clipfoo;

/// <summary>
/// Hidden message-only window that owns the global hotkey registration and raises an event
/// when it fires.
/// </summary>
internal sealed class HotKeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotKeyId = 0xB001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public event Action? HotKeyPressed;

    public HotKeyWindow()
    {
        CreateHandle(new CreateParams());
    }

    /// <summary>Register the given modifiers + virtual key. Returns false if the combo is taken.</summary>
    public bool Register(uint modifiers, uint virtualKey)
    {
        UnregisterHotKey(Handle, HotKeyId);
        return RegisterHotKey(Handle, HotKeyId, modifiers | Hotkey.MOD_NOREPEAT, virtualKey);
    }

    /// <summary>Temporarily release the hotkey (e.g. while the user is assigning a new one).</summary>
    public void Unregister() => UnregisterHotKey(Handle, HotKeyId);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotKeyId)
            HotKeyPressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterHotKey(Handle, HotKeyId);
        DestroyHandle();
    }
}
