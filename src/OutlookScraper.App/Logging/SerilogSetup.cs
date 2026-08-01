using System.IO;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
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
    /// <summary>
    /// The live minimum level. A switch rather than a fixed <c>MinimumLevel</c> because
    /// verbose logging is most wanted precisely when something is already going wrong,
    /// and demanding a restart to turn it on would lose the very arrivals being chased.
    /// </summary>
    private static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Information);

    /// <summary>Takes effect on the next log call — no restart, no re-created factory.</summary>
    public static void SetVerbose(bool verbose) =>
        LevelSwitch.MinimumLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Information;

    public static ILoggerFactory Create(string logDirectory, bool verbose = false)
    {
        Directory.CreateDirectory(logDirectory);
        SetVerbose(verbose);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
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
