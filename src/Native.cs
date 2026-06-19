using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Capper;

/// <summary>
/// Win32 / WinRT interop needed to identify the active window and bridge it into
/// Windows.Graphics.Capture + Direct3D11.
/// </summary>
internal static class Native
{
    // --- Foreground window identification ---

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    public static int GetWindowProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out int pid);
        return pid;
    }

    public static IntPtr GetActiveWindow()
    {
        var hwnd = GetForegroundWindow();
        return hwnd == IntPtr.Zero || !IsWindow(hwnd) ? IntPtr.Zero : hwnd;
    }

    public static bool IsCapturableWindow(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && IsWindow(hwnd) && IsWindowVisible(hwnd);

    // --- Monitor identification ---

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    /// <summary>The monitor a window is on (nearest), or the primary monitor as a fallback.</summary>
    public static IntPtr GetMonitorForWindow(IntPtr hwnd)
    {
        IntPtr mon = hwnd != IntPtr.Zero ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
        return mon != IntPtr.Zero ? mon : MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
    }

    // --- GraphicsCaptureItem from an HWND ---

    private static readonly Guid GraphicsCaptureItemIid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, [In] ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    /// <summary>Resolve the GraphicsCaptureItem activation factory's interop interface
    /// (classic COM marshaling — WinRT projection can't reach these interop methods).</summary>
    private static IGraphicsCaptureItemInterop GetCaptureInterop()
    {
        const string classId = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Guid interopIid = typeof(IGraphicsCaptureItemInterop).GUID;

        WindowsCreateString(classId, classId.Length, out IntPtr hClass);
        IntPtr factoryPtr;
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hClass, ref interopIid, out factoryPtr));
        }
        finally
        {
            WindowsDeleteString(hClass);
        }

        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
        Marshal.Release(factoryPtr);
        return interop;
    }

    private static GraphicsCaptureItem? ItemFromAbi(IntPtr abi)
    {
        if (abi == IntPtr.Zero) return null;
        try { return GraphicsCaptureItem.FromAbi(abi); }
        finally { Marshal.Release(abi); }
    }

    public static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
    {
        Guid itemIid = GraphicsCaptureItemIid;
        return ItemFromAbi(GetCaptureInterop().CreateForWindow(hwnd, ref itemIid));
    }

    public static GraphicsCaptureItem? CreateItemForMonitor(IntPtr hmonitor)
    {
        Guid itemIid = GraphicsCaptureItemIid;
        return ItemFromAbi(GetCaptureInterop().CreateForMonitor(hmonitor, ref itemIid));
    }

    // --- Direct3D <-> WinRT bridge ---

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>Wrap a DXGI device pointer in a WinRT IDirect3DDevice for the capture frame pool.</summary>
    public static IDirect3DDevice CreateDirect3DDevice(IntPtr dxgiDevicePtr)
    {
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePtr, out IntPtr inspectable);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        try
        {
            return MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11SurfaceFromDXGISurface", SetLastError = true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

    /// <summary>Wrap a DXGI surface pointer in a WinRT IDirect3DSurface (e.g. as a
    /// CopyFrameToVideoSurface destination for MediaPlayer frame-server playback).</summary>
    public static IDirect3DSurface CreateDirect3DSurface(IntPtr dxgiSurfacePtr)
    {
        int hr = CreateDirect3D11SurfaceFromDXGISurface(dxgiSurfacePtr, out IntPtr inspectable);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        try
        {
            return MarshalInspectable<IDirect3DSurface>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>COM interop to pull the underlying DXGI/D3D resource out of a captured surface.</summary>
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    /// <summary>Get the native ID3D11Texture2D pointer backing a captured frame surface.</summary>
    public static IntPtr GetDxgiInterface(IDirect3DSurface surface, Guid iid)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        return access.GetInterface(ref iid);
    }
}
