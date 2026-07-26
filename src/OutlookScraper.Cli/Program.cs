using System.Text.Json;
using OutlookScraper.Core.Blacklist;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Ollama;
using OutlookScraper.Core.Settings;
using OutlookScraper.Core.Text;
using OutlookScraper.Core.Time;

namespace OutlookScraper.Cli;

/// <summary>
/// Cross-platform harness for the classification pipeline.
/// </summary>
/// <remarks>
/// This exists so prompts, the schema and the blacklist thresholds can be tuned
/// against real fixture emails on any machine with Ollama, without Windows, Outlook,
/// or a Google account anywhere in the loop. Iterating on prompt quality through the
/// full tray app would be miserable.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "classify" => await ClassifyAsync(args.Skip(1).ToArray()),
                "tags" => await TagsAsync(),
                "match" => Match(args.Skip(1).ToArray()),
                "schema" => Schema(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage() => Console.WriteLine(
        """
        outlookscraper — dev harness for the classification pipeline

          classify <file|dir> [--model M] [--zone IANA]
              Run the classifier over one .txt fixture or every .txt in a directory.
              Fixture format: first line "From: Name <addr>", second "Subject: ...",
              optional third "Received: 2026-07-20T14:02:00-05:00", then a blank line
              and the body.

          tags
              List models installed in the local Ollama.

          match <tagA> <tagB>
              Show how the blacklist cascade compares two topic tags: normalized keys
              and Jaccard overlap. Use this to sanity-check a threshold.

          schema
              Print the JSON schema sent to Ollama's format parameter.
        """);

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'");
        PrintUsage();
        return 1;
    }

    private static async Task<int> ClassifyAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("classify needs a file or directory");
            return 1;
        }

        var settings = new AppSettings();
        settings.Ollama.Model = ValueOf(args, "--model") ?? settings.Ollama.Model;
        settings.Calendar.TimeZone = ValueOf(args, "--zone") ?? TimeZoneResolution.LocalIanaId();

        var paths = ResolveFixtures(args[0]);

        if (paths.Count == 0)
        {
            Console.Error.WriteLine($"no .txt fixtures found at {args[0]}");
            return 1;
        }

        using var http = new HttpClient { BaseAddress = new Uri(settings.Ollama.BaseUrl) };
        http.Timeout = TimeSpan.FromSeconds(settings.Ollama.RequestTimeoutSeconds);

        var client = new OllamaClient(http);
        var classifier = new OllamaClassifier(
            client,
            settings.Ollama,
            new PromptBuilder(settings.Calendar.TimeZone),
            Core.Abstractions.SystemClock.Instance);

        var preparer = new EmailPreparer(settings.Ollama);
        var resolver = new EventTimeResolver(settings.Calendar);

        var positives = 0;

        foreach (var path in paths)
        {
            var email = ParseFixture(path);
            var cleaned = preparer.Prepare(email);

            Console.WriteLine(new string('─', 72));
            Console.WriteLine($"{Path.GetFileName(path)}  |  {email.Subject}");

            ClassificationResult result;

            try
            {
                result = await classifier.ClassifyAsync(cleaned, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAILED: {ex.Message}");
                continue;
            }

            var qualifies = result.QualifiesAsFreeFoodEvent(settings.Ollama.ConfidenceThreshold);

            if (qualifies)
            {
                positives++;
            }

            Console.WriteLine($"  free food : {(qualifies ? "YES" : "no")}  " +
                              $"(event={result.IsEvent}, food={result.HasFreeFood}, " +
                              $"confidence={result.Confidence.ToString().ToLowerInvariant()})");
            Console.WriteLine($"  category  : {result.Category}");
            Console.WriteLine($"  topic tag : {result.TopicTag}  →  key '{TagNormalizer.Normalize(result.TopicTag)}'");
            Console.WriteLine($"  reason    : {result.Reason}");

            if (qualifies)
            {
                Console.WriteLine($"  title     : {result.Title}");
                Console.WriteLine($"  food      : {result.FoodDescription}");
                Console.WriteLine($"  location  : {result.Location}");

                var outcome = resolver.Resolve(result, email.ReceivedLocal);

                Console.WriteLine(outcome.IsResolved
                    ? $"  when      : {outcome.Time!.Start:yyyy-MM-dd HH:mm zzz} → " +
                      $"{outcome.Time.End:HH:mm} ({outcome.Time.IanaTimeZone})" +
                      (result.DateIsExplicit ? "" : "  [date inferred — needs review]")
                    : $"  when      : unresolved ({outcome.Problem})");
            }
        }

        Console.WriteLine(new string('─', 72));
        Console.WriteLine($"{positives} of {paths.Count} classified as free-food events.");

        return 0;
    }

    private static async Task<int> TagsAsync()
    {
        var settings = new OllamaSettings();
        using var http = new HttpClient { BaseAddress = new Uri(settings.BaseUrl) };

        var models = await new OllamaClient(http).ListModelsAsync(CancellationToken.None);

        foreach (var model in models)
        {
            Console.WriteLine(model);
        }

        return 0;
    }

    /// <summary>
    /// Shows the deterministic half of the cascade for two tags. This is the tool for
    /// answering "why was this suppressed?" without attaching a debugger.
    /// </summary>
    private static int Match(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("match needs two tags");
            return 1;
        }

        var keyA = TagNormalizer.Normalize(args[0]);
        var keyB = TagNormalizer.Normalize(args[1]);
        var jaccard = TokenSimilarity.Jaccard(args[0], args[1]);
        var thresholds = new BlacklistSettings();

        Console.WriteLine($"  '{args[0]}'  →  key '{keyA}'");
        Console.WriteLine($"  '{args[1]}'  →  key '{keyB}'");
        Console.WriteLine();
        Console.WriteLine($"  stage 1 (exact key)     : {(keyA == keyB && keyA.Length > 0 ? "MATCH" : "no")}");
        Console.WriteLine($"  stage 2 (jaccard)       : {jaccard:F3} " +
                          $"(threshold {thresholds.TokenThreshold:F2}) " +
                          $"{(jaccard >= thresholds.TokenThreshold ? "MATCH" : "no")}");
        Console.WriteLine($"  stage 3 (embeddings)    : needs a running model; " +
                          $"soft {thresholds.SemanticSoftThreshold:F2} / " +
                          $"strong {thresholds.SemanticStrongThreshold:F2}");

        return 0;
    }

    private static int Schema()
    {
        Console.WriteLine(ClassificationSchema.ToJson());
        return 0;
    }

    private static List<string> ResolveFixtures(string path) => Directory.Exists(path)
        ? Directory.GetFiles(path, "*.txt").OrderBy(p => p).ToList()
        : File.Exists(path) ? [path] : [];

    /// <summary>
    /// Reads the simple fixture format. Anything unrecognised in the header is treated
    /// as the start of the body, so a plain text file still works.
    /// </summary>
    internal static RawEmail ParseFixture(string path)
    {
        var lines = File.ReadAllLines(path);
        string sender = "Unknown", address = "unknown@example.edu", subject = "(no subject)";
        var received = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var index = 0;

        for (; index < lines.Length; index++)
        {
            var line = lines[index];

            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                break;
            }

            if (line.StartsWith("From:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["From:".Length..].Trim();
                var open = value.IndexOf('<');

                if (open >= 0)
                {
                    sender = value[..open].Trim();
                    address = value[(open + 1)..].TrimEnd('>').Trim();
                }
                else
                {
                    sender = value;
                }
            }
            else if (line.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
            {
                subject = line["Subject:".Length..].Trim();
            }
            else if (line.StartsWith("Received:", StringComparison.OrdinalIgnoreCase))
            {
                if (DateTimeOffset.TryParse(
                        line["Received:".Length..].Trim(),
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out var parsed))
                {
                    received = parsed;
                }
            }
            else
            {
                break;
            }
        }

        var body = string.Join('\n', lines.Skip(index));

        return new RawEmail(
            Path.GetFileNameWithoutExtension(path),
            "FIXTURE",
            subject,
            sender,
            address,
            received,
            "IPM.Note",
            body,
            null,
            false,
            "Inbox");
    }

    private static string? ValueOf(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
