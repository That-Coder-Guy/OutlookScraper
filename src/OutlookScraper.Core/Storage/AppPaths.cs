namespace OutlookScraper.Core.Storage;

/// <summary>
/// Every file the app owns, in one place.
/// </summary>
/// <remarks>
/// Resolves to <c>%LOCALAPPDATA%\OutlookScraper</c> on Windows and to the XDG
/// equivalent elsewhere, which is what lets the CLI harness and the test suite run
/// the real storage code on Linux.
/// </remarks>
public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OutlookScraper");
    }

    public string Root { get; }

    public string DatabasePath => Path.Combine(Root, "data.db");

    public string SettingsPath => Path.Combine(Root, "settings.json");

    /// <summary>Dropped here by the user after downloading it from Google Cloud Console.</summary>
    public string ClientSecretPath => Path.Combine(Root, "client_secret.json");

    /// <summary>OAuth token store. Contents are DPAPI-encrypted on Windows.</summary>
    public string TokenDirectory => Path.Combine(Root, "google-token");

    public string LogDirectory => Path.Combine(Root, "logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(TokenDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
