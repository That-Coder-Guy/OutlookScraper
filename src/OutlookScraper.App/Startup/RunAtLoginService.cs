using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace OutlookScraper.App.Startup;

/// <summary>
/// Registers the app to start with the user's session.
/// </summary>
/// <remarks>
/// Uses the per-user Run key rather than a scheduled task or a service: it needs no
/// administrator rights, and it shows up under Task Manager → Startup so the user can
/// disable it the way they would disable anything else.
///
/// Kept strictly separate from the toast Start Menu shortcut. The two look similar and
/// serve completely different purposes, and conflating them breaks both.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RunAtLoginService(ILogger<RunAtLoginService>? logger = null)
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OutlookScraper";
    private const string TrayArgument = "--tray";

    private readonly ILogger<RunAtLoginService>? _logger = logger;

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

        if (key is null)
        {
            return;
        }

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        if (CommandLine() is { } command)
        {
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
    }

    /// <summary>
    /// Rewrites the entry when the executable has moved, the same way the toast
    /// registration is repaired. Without it, moving the app leaves a Run entry pointing
    /// at nothing.
    /// </summary>
    public void RepairIfMoved()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

        if (key?.GetValue(ValueName) is not string existing || CommandLine() is not { } expected)
        {
            return;
        }

        if (!string.Equals(existing, expected, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation("Repairing run-at-login entry after the app moved.");
            key.SetValue(ValueName, expected, RegistryValueKind.String);
        }
    }

    private static string? CommandLine() =>
        Environment.ProcessPath is { } path ? $"\"{path}\" {TrayArgument}" : null;
}
