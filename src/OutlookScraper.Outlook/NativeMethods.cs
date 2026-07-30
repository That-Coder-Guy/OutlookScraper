using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OutlookScraper.Outlook;

/// <summary>
/// P/Invokes for attaching to an already-running Outlook.
/// </summary>
/// <remarks>
/// <c>Marshal.GetActiveObject</c> exists in .NET Framework but was never ported to
/// .NET Core / .NET 5+, so the COM running-object-table lookup has to be declared by
/// hand. This is the single most common thing that catches people porting Outlook
/// automation to modern .NET.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    /// <summary>Outlook is not currently running (MK_E_UNAVAILABLE).</summary>
    private const int MkEUnavailable = unchecked((int)0x800401E3);

    /// <summary>Win32 <c>MSG</c>. Field order and size must match exactly.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>Returns &gt;0 for a message, 0 for WM_QUIT, -1 on error.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMessage(out Msg message, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(
        out Msg message, IntPtr hWnd, uint filterMin, uint filterMax, uint removeMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessage(
        uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CLSIDFromProgID(
        [MarshalAs(UnmanagedType.LPWStr)] string progId, out Guid clsid);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(
        ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    /// <summary>
    /// Returns the running Outlook application, or null if it is not running.
    /// </summary>
    /// <remarks>
    /// Deliberately never falls back to <c>new Application()</c>. Constructing one
    /// launches OUTLOOK.EXE, and if the app is still holding runtime callable wrappers
    /// when the user "closes" Outlook, the process lingers invisibly — the classic
    /// zombie-Outlook bug. Attach-only makes that structurally impossible.
    /// </remarks>
    public static object? TryGetRunningOutlook()
    {
        try
        {
            CLSIDFromProgID("Outlook.Application", out var clsid);
            GetActiveObject(ref clsid, IntPtr.Zero, out var instance);
            return instance;
        }
        catch (COMException ex) when (ex.HResult == MkEUnavailable)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }
}
