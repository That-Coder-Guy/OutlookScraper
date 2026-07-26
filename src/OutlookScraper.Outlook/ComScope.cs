using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OutlookScraper.Outlook;

/// <summary>
/// Tracks short-lived COM objects and releases them in reverse order.
/// </summary>
/// <remarks>
/// Every dotted property access on the Outlook object model mints a new runtime
/// callable wrapper, and every one of them has to be released. That is why the code
/// here never writes chains like <c>app.Session.GetDefaultFolder(...)</c> — the
/// intermediate <c>NameSpace</c> would leak with no reference left to free it.
///
/// Reverse order matters: children are released before the parents they were obtained
/// from.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ComScope : IDisposable
{
    private readonly List<object> _tracked = [];

    /// <summary>Registers an object for release and returns it, so calls can be nested.</summary>
    public T Track<T>(T comObject) where T : class
    {
        if (comObject is not null)
        {
            _tracked.Add(comObject);
        }

        return comObject;
    }

    public void Dispose()
    {
        for (var i = _tracked.Count - 1; i >= 0; i--)
        {
            Release(_tracked[i]);
        }

        _tracked.Clear();
    }

    /// <summary>
    /// Releases a single wrapper. Safe to call on anything: a non-COM object or an
    /// already-released wrapper is ignored rather than throwing during cleanup.
    /// </summary>
    public static void Release(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(comObject);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or ArgumentException)
        {
            // Already gone, or Outlook died mid-teardown. Nothing useful to do.
        }
    }

    /// <summary>
    /// Forces the CLR to finish releasing wrappers. Called twice on purpose: the first
    /// pass frees wrappers whose finalizers the second pass then needs to observe.
    /// </summary>
    public static void CollectReleasedWrappers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
