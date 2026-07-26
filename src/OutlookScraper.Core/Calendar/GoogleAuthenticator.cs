using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using OutlookScraper.Core.Storage;

namespace OutlookScraper.Core.Calendar;

/// <summary>
/// Desktop OAuth against Google Calendar.
/// </summary>
/// <remarks>
/// Uses the loopback flow: <c>GoogleWebAuthorizationBroker</c> spins up a local
/// listener on an ephemeral port and opens the system browser. That requires the OAuth
/// client to be registered as an "Desktop app" in Google Cloud Console — the old
/// out-of-band <c>urn:ietf:wg:oauth:2.0:oob</c> flow has been shut off and must not be used.
///
/// The scope is <c>calendar.events</c> rather than full <c>calendar</c>. The tradeoff
/// is that the app cannot enumerate the user's calendar list, so the target calendar is
/// a text setting defaulting to "primary". Broadening the grant purely to populate a
/// dropdown is a bad deal.
/// </remarks>
public sealed class GoogleAuthenticator(AppPaths paths, IDataStore? dataStore = null)
{
    private readonly AppPaths _paths = paths;
    private readonly IDataStore? _dataStore = dataStore;

    /// <summary>Insert, update and delete events. Deliberately not full calendar access.</summary>
    private static readonly string[] Scopes = [CalendarService.Scope.CalendarEvents];

    private const string ApplicationName = "OutlookScraper";

    /// <summary>True once the user has dropped their downloaded client secret into place.</summary>
    public bool HasClientSecret => File.Exists(_paths.ClientSecretPath);

    public async Task<CalendarService> GetServiceAsync(CancellationToken ct)
    {
        if (!HasClientSecret)
        {
            throw new InvalidOperationException(
                $"No Google client secret found at {_paths.ClientSecretPath}. " +
                "See docs/SETUP.md for how to create one.");
        }

        var credential = await AuthorizeAsync(ct);

        return new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName,
        });
    }

    private async Task<UserCredential> AuthorizeAsync(CancellationToken ct)
    {
        await using var stream = File.OpenRead(_paths.ClientSecretPath);

        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            Scopes,
            user: "default",
            ct,
            _dataStore ?? new FileDataStore(_paths.TokenDirectory, fullPath: true));
    }

    /// <summary>Forgets the stored token so the next call re-prompts for consent.</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var store = _dataStore ?? new FileDataStore(_paths.TokenDirectory, fullPath: true);
        await store.ClearAsync();
    }
}
