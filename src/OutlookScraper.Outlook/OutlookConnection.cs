using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using OutlookScraper.Core.Models;
// Aliased as `Interop`, deliberately not as `Outlook`. This project's own
// namespace is OutlookScraper.Outlook, so an alias named `Outlook` loses to the
// enclosing namespace: `Outlook.MailItem` resolves to OutlookScraper.Outlook.MailItem,
// which does not exist. Renaming the alias is the fix.
using Interop = Microsoft.Office.Interop.Outlook;

namespace OutlookScraper.Outlook;

/// <summary>
/// Holds the live Outlook object graph and its event subscriptions.
/// </summary>
/// <remarks>
/// All members must be touched only from the STA thread.
///
/// <b>Every COM reference here is a field, and that is load-bearing rather than
/// stylistic.</b> The classic failure is <c>folder.Items.ItemAdd += handler</c> with
/// <c>Items</c> as a local: the wrapper becomes unreferenced the moment the statement
/// ends, the GC collects it, the connection point dies, and events stop arriving with
/// no error of any kind. Holding <c>Items</c> — and the handler delegates — for the
/// process lifetime is the only thing keeping the subscription alive.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class OutlookConnection(ILogger? logger = null)
{
    private readonly ILogger? _logger = logger;

    private Interop.Application? _application;
    private Interop.NameSpace? _session;

    // Held for the lifetime of the connection. See the class remarks.
    private readonly List<Interop.MAPIFolder> _folders = [];
    private readonly List<Interop.Items> _items = [];
    private readonly List<Interop.ItemsEvents_ItemAddEventHandler> _itemAddHandlers = [];

    private Interop.ApplicationEvents_11_NewMailExEventHandler? _newMailHandler;
    private Interop.ApplicationEvents_11_QuitEventHandler? _quitHandler;

    public bool IsConnected => _application is not null;

    /// <summary>Raised with an EntryID whenever Outlook reports new mail.</summary>
    public event Action<string>? EntryIdArrived;

    /// <summary>Raised when Outlook signals it is shutting down.</summary>
    public event Action? HostQuitting;

    /// <summary>
    /// Attaches to a running Outlook. Returns false when Outlook is not running, which
    /// is an ordinary state rather than an error.
    /// </summary>
    public bool TryConnect(IReadOnlyList<string> watchedFolders)
    {
        if (NativeMethods.TryGetRunningOutlook() is not Interop.Application application)
        {
            return false;
        }

        _application = application;
        _session = application.Session;

        SubscribeQuit(application);
        SubscribeNewMailEx(application);
        SubscribeFolders(watchedFolders);

        _logger?.LogInformation(
            "Connected to Outlook; watching {FolderCount} folder(s).", _folders.Count);

        return true;
    }

    private void SubscribeQuit(Interop.Application application)
    {
        // Sinking Quit gives a clean teardown before the RPC channel dies, which is far
        // nicer than discovering it through an RPC_E_DISCONNECTED storm.
        _quitHandler = () => HostQuitting?.Invoke();

        // Application.Quit is BOTH a method and an event on the interop type, so a
        // bare `application.Quit +=` binds to the method group and will not compile.
        // The cast to the event interface disambiguates it.
        ((Interop.ApplicationEvents_11_Event)application).Quit += _quitHandler;
    }

    private void SubscribeNewMailEx(Interop.Application application)
    {
        // Cheap redundancy for the default inbox. The payload is a space-delimited list
        // of EntryIDs rather than the items themselves.
        _newMailHandler = entryIds =>
        {
            foreach (var entryId in entryIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var id = entryId.Trim();
                _logger?.LogInformation("NewMailEx fired for [{Id}].", Tail(id));
                EntryIdArrived?.Invoke(id);
            }
        };

        application.NewMailEx += _newMailHandler;
    }

    private void SubscribeFolders(IReadOnlyList<string> watchedFolders)
    {
        foreach (var folder in ResolveFolders(watchedFolders))
        {
            _folders.Add(folder);

            var items = folder.Items;
            _items.Add(items);

            // The delegate is held too — an anonymous handler that goes out of scope is
            // just as collectable as the Items wrapper.
            Interop.ItemsEvents_ItemAddEventHandler handler = OnItemAdd;
            _itemAddHandlers.Add(handler);
            items.ItemAdd += handler;

            _logger?.LogInformation(
                "Subscribed to ItemAdd on '{Folder}' ({Count} items).", folder.Name, items.Count);
        }
    }

    private IEnumerable<Interop.MAPIFolder> ResolveFolders(IReadOnlyList<string> watchedFolders)
    {
        if (_session is null)
        {
            yield break;
        }

        if (watchedFolders.Count == 0)
        {
            yield return _session.GetDefaultFolder(Interop.OlDefaultFolders.olFolderInbox);
            yield break;
        }

        foreach (var name in watchedFolders)
        {
            Interop.MAPIFolder? resolved = null;

            try
            {
                resolved = ResolveByPath(name);
            }
            catch (COMException ex)
            {
                _logger?.LogWarning("Could not resolve watched folder '{Folder}': {Message}", name, ex.Message);
            }

            if (resolved is not null)
            {
                yield return resolved;
            }
        }
    }

    /// <summary>Resolves a backslash-separated path beneath the default inbox's store.</summary>
    private Interop.MAPIFolder? ResolveByPath(string path)
    {
        var inbox = _session!.GetDefaultFolder(Interop.OlDefaultFolders.olFolderInbox);

        if (string.IsNullOrWhiteSpace(path) ||
            path.Equals("Inbox", StringComparison.OrdinalIgnoreCase))
        {
            return inbox;
        }

        Interop.MAPIFolder current = inbox;

        foreach (var segment in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals("Inbox", StringComparison.OrdinalIgnoreCase) && current == inbox)
            {
                continue;
            }

            current = current.Folders[segment];
        }

        return current;
    }

    /// <summary>
    /// Reads only the EntryID and returns. A slow handler blocks Outlook's own UI, so
    /// every expensive read is deferred to the worker, which re-opens the item by id.
    /// </summary>
    private void OnItemAdd(object item)
    {
        try
        {
            // Calendar items, tasks and reports land in inboxes too.
            if (item is not Interop.MailItem mail)
            {
                _logger?.LogDebug("ItemAdd fired for a non-mail item; ignoring.");
                return;
            }

            var entryId = mail.EntryID;

            // The lowest-latency path, and the one that proves live delivery works at
            // all. Logged at Information precisely because its silent failure — a
            // garbage-collected event sink — is otherwise indistinguishable from an
            // idle mailbox.
            _logger?.LogInformation("ItemAdd fired for [{Id}].", Tail(entryId));

            EntryIdArrived?.Invoke(entryId);
        }
        catch (COMException ex)
        {
            _logger?.LogDebug("ItemAdd handler failed: {Message}", ex.Message);
        }
        finally
        {
            ComScope.Release(item);
        }
    }

    /// <summary>Re-opens a message by id. Returns null if it has since moved or been deleted.</summary>
    public RawEmail? GetByEntryId(string entryId)
    {
        if (_session is null)
        {
            return null;
        }

        using var scope = new ComScope();

        try
        {
            var item = scope.Track(_session.GetItemFromID(entryId));

            if (item is not Interop.MailItem mail)
            {
                return null;
            }

            return MailItemMapper.Map(mail, FolderNameOf(mail, scope));
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls everything received since the given time across all watched folders.
    /// </summary>
    public IReadOnlyList<RawEmail> Sweep(DateTimeOffset sinceLocal, int max)
    {
        var results = new List<RawEmail>();

        foreach (var folder in _folders)
        {
            if (results.Count >= max)
            {
                break;
            }

            SweepFolder(folder, sinceLocal, max, results);
        }

        return results;
    }

    private void SweepFolder(
        Interop.MAPIFolder folder, DateTimeOffset sinceLocal, int max, List<RawEmail> results)
    {
        using var scope = new ComScope();

        try
        {
            // Items and Restrict never return null for a live folder; a failure surfaces
            // as the COMException caught below instead.
            var items = scope.Track(folder.Items)!;
            items.Sort("[ReceivedTime]", Descending: true);

            var filtered = scope.Track(
                items.Restrict(RestrictFilterBuilder.ReceivedSinceWithOverlap(sinceLocal)))!;

            var folderName = folder.Name;

            // Outlook collections are 1-based, and foreach holds enumerator wrappers
            // longer than indexing does.
            for (var i = 1; i <= filtered.Count && results.Count < max; i++)
            {
                var entry = scope.Track(filtered[i]);

                if (entry is Interop.MailItem mail)
                {
                    results.Add(MailItemMapper.Map(mail, folderName));
                }
            }
        }
        catch (COMException ex)
        {
            _logger?.LogWarning("Sweep of folder failed: {Message}", ex.Message);
        }
    }

    private static string FolderNameOf(Interop.MailItem mail, ComScope scope)
    {
        try
        {
            return scope.Track(mail.Parent as Interop.MAPIFolder)?.Name ?? "";
        }
        catch (COMException)
        {
            return "";
        }
    }

    /// <summary>Last 10 characters of an EntryID — enough to correlate, short enough to read.</summary>
    private static string Tail(string entryId) =>
        string.IsNullOrEmpty(entryId) ? "?" :
        entryId.Length <= 10 ? entryId : entryId[^10..];

    /// <summary>Cheap liveness probe. Throws if the RPC channel is gone.</summary>
    public void Probe() => _ = _application?.Version;

    /// <summary>
    /// Unsubscribes every sink and releases the whole graph.
    /// </summary>
    /// <remarks>
    /// Order matters: handlers come off before their objects are released. Releasing an
    /// object that still has a live connection point is how RPC_E_DISCONNECTED storms
    /// start.
    /// </remarks>
    public void Teardown()
    {
        for (var i = 0; i < _items.Count; i++)
        {
            try
            {
                _items[i].ItemAdd -= _itemAddHandlers[i];
            }
            catch (COMException)
            {
                // Outlook already gone.
            }
        }

        _itemAddHandlers.Clear();

        if (_application is not null)
        {
            try
            {
                if (_newMailHandler is not null)
                {
                    _application.NewMailEx -= _newMailHandler;
                }

                if (_quitHandler is not null)
                {
                    ((Interop.ApplicationEvents_11_Event)_application).Quit -= _quitHandler;
                }
            }
            catch (COMException)
            {
                // Outlook already gone.
            }
        }

        _newMailHandler = null;
        _quitHandler = null;

        foreach (var items in _items)
        {
            ComScope.Release(items);
        }

        _items.Clear();

        foreach (var folder in _folders)
        {
            ComScope.Release(folder);
        }

        _folders.Clear();

        ComScope.Release(_session);
        _session = null;

        // Never Quit() the application — this app attaches to the user's Outlook and
        // does not own its lifetime.
        ComScope.Release(_application);
        _application = null;

        ComScope.CollectReleasedWrappers();
    }
}
