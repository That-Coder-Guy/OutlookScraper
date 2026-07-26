using System.IO;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace OutlookScraper.App.Logging;

/// <summary>
/// Rolling file logging under the app's local data directory.
/// </summary>
/// <remarks>
/// A tray app has no console, so when something goes wrong — a COM disconnect, a
/// malformed model response, an OAuth failure — the log file is the only evidence.
/// The Settings window exposes an "open log folder" button for that reason.
/// </remarks>
public static class SerilogSetup
{
    public static ILoggerFactory Create(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logDirectory, "outlookscraper-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        return new SerilogLoggerFactory(Log.Logger, dispose: true);
    }

    public static void Shutdown() => Log.CloseAndFlush();
}
