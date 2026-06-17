using System.Runtime.InteropServices;
using System.Text;

namespace Clipfoo;

/// <summary>
/// Tunes the Media Foundation H.264 encoder for better quality-per-byte via its <c>ICodecAPI</c>.
/// The encoder otherwise defaults to Baseline profile with no B-frames; combined with setting the
/// High profile on the output media type, this enables B-frames and a quality-leaning speed setting.
/// Every setting is validated with <c>IsSupported</c> and applied best-effort, so an unsupported
/// property (or a wrong GUID) is skipped and logged rather than breaking encoding.
/// </summary>
internal static class H264EncoderConfig
{
    public static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    // From codecapi.h
    private static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    private static readonly Guid AVEncVideoMaxNumRefFrame = new("964829ed-94f9-43b4-b74d-ef40944b69a0");

    /// <summary>Configure the encoder behind <paramref name="codecApiPtr"/> (an ICodecAPI*, ownership
    /// transferred). Returns a short log of what applied.</summary>
    public static string Apply(IntPtr codecApiPtr)
    {
        var codec = (ICodecAPI)Marshal.GetObjectForIUnknown(codecApiPtr);
        Marshal.Release(codecApiPtr);
        var log = new StringBuilder();
        try
        {
            SetU32(codec, AVEncMPVDefaultBPictureCount, 1, log, "bframes");
            SetU32(codec, AVEncVideoMaxNumRefFrame, 2, log, "refframes");
            SetU32(codec, AVEncCommonQualityVsSpeed, 70, log, "qvs");
        }
        finally
        {
            try { Marshal.ReleaseComObject(codec); } catch { }
        }
        return log.ToString().TrimEnd();
    }

    private static void SetU32(ICodecAPI codec, Guid api, uint value, StringBuilder log, string name)
    {
        try
        {
            if (codec.IsSupported(ref api) != 0) { log.Append($"{name}=unsupported; "); return; }
            object boxed = value; // marshals as VARIANT VT_UI4
            int hr = codec.SetValue(ref api, ref boxed);
            log.Append(hr == 0 ? $"{name}=ok; " : $"{name}=hr0x{hr:X}; ");
        }
        catch (Exception ex) { log.Append($"{name}=ex({ex.GetType().Name}); "); }
    }

    // Minimal ICodecAPI: methods declared in vtable order up to SetValue. object params marshal as VARIANT.
    [ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        [PreserveSig] int IsSupported(ref Guid api);
        [PreserveSig] int IsModifiable(ref Guid api);
        [PreserveSig] int GetParameterRange(ref Guid api, out object min, out object max, out object step);
        [PreserveSig] int GetParameterValues(ref Guid api, out IntPtr values, out uint count);
        [PreserveSig] int GetDefaultValue(ref Guid api, out object value);
        [PreserveSig] int GetValue(ref Guid api, out object value);
        [PreserveSig] int SetValue(ref Guid api, ref object value);
    }
}
