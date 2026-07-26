using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Google.Apis.Util.Store;
using Newtonsoft.Json;
using OutlookScraper.Core.Abstractions;

namespace OutlookScraper.App.Security;

/// <summary>Encrypts at rest using the current user's DPAPI key.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
}

/// <summary>
/// A Google <see cref="IDataStore"/> that encrypts what it writes.
/// </summary>
/// <remarks>
/// The stock <c>FileDataStore</c> writes the OAuth refresh token to disk in plaintext.
/// A refresh token is a long-lived credential for the user's calendar, so it is worth
/// the very small amount of code to put it behind DPAPI instead.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiDataStore(string directory, ISecretProtector protector) : IDataStore
{
    private readonly string _directory = directory;
    private readonly ISecretProtector _protector = protector;

    private string PathFor(string key) =>
        Path.Combine(_directory, $"{Uri.EscapeDataString(key)}.dat");

    public Task StoreAsync<T>(string key, T value)
    {
        Directory.CreateDirectory(_directory);

        var json = JsonConvert.SerializeObject(value);
        var encrypted = _protector.Protect(Encoding.UTF8.GetBytes(json));

        File.WriteAllBytes(PathFor(key), encrypted);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var path = PathFor(key);

        if (!File.Exists(path))
        {
            return Task.FromResult<T>(default!);
        }

        try
        {
            var decrypted = _protector.Unprotect(File.ReadAllBytes(path));
            var json = Encoding.UTF8.GetString(decrypted);

            return Task.FromResult(JsonConvert.DeserializeObject<T>(json)!);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Copied from another machine or another user account, so it cannot be
            // decrypted. Treat it as absent and re-prompt for consent.
            return Task.FromResult<T>(default!);
        }
    }

    public Task DeleteAsync<T>(string key)
    {
        var path = PathFor(key);

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.GetFiles(_directory, "*.dat"))
            {
                File.Delete(file);
            }
        }

        return Task.CompletedTask;
    }
}
