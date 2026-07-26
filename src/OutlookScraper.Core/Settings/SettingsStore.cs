using System.Text.Json;

namespace OutlookScraper.Core.Settings;

/// <summary>
/// Loads and saves <c>settings.json</c>.
/// </summary>
/// <remarks>
/// Writes go to a temporary file and are then moved into place, so a crash mid-save
/// leaves the previous settings intact rather than a truncated file the app cannot
/// start from.
/// </remarks>
public sealed class SettingsStore(string path)
{
    private readonly string _path = path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Returns defaults when the file is missing or unreadable. A corrupt settings
    /// file should degrade to defaults, never stop the app from starting.
    /// </summary>
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);

        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, ct)
                   ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);

        try
        {
            var directory = Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = _path + ".tmp";

            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, settings, Options, ct);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
