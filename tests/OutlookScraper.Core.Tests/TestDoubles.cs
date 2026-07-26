using OutlookScraper.Core.Abstractions;
using OutlookScraper.Core.Models;

namespace OutlookScraper.Core.Tests;

/// <summary>A clock the test controls outright.</summary>
public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>
/// Returns pre-canned vectors keyed by the exact text embedded, so cascade tests can
/// dictate cosine similarity precisely instead of depending on a real model.
/// </summary>
public sealed class FakeEmbeddingProvider(string modelName = "fake-embed") : IEmbeddingProvider
{
    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);

    public string ModelName { get; } = modelName;

    public bool Available { get; set; } = true;

    public float[]? Fallback { get; set; }

    public void Register(string text, float[] vector) => _vectors[text] = vector;

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(Available);

    public Task<float[]?> EmbedAsync(string text, CancellationToken ct)
    {
        if (!Available)
        {
            return Task.FromResult<float[]?>(null);
        }

        return Task.FromResult(_vectors.TryGetValue(text, out var vector) ? vector : Fallback);
    }
}

/// <summary>Returns a fixed verdict, or throws, without touching the network.</summary>
public sealed class FakeClassifier(ClassificationResult? result = null) : IClassifier
{
    public ClassificationResult? Result { get; set; } = result;

    public Exception? Throws { get; set; }

    public int Calls { get; private set; }

    public Task<ClassificationResult> ClassifyAsync(CleanedEmail email, CancellationToken ct)
    {
        Calls++;

        if (Throws is not null)
        {
            throw Throws;
        }

        return Task.FromResult(Result ?? TestData.NotAnEvent());
    }
}

/// <summary>Records what would have been shown, so the pipeline can be asserted on.</summary>
public sealed class RecordingNotifier : INotifier
{
    public List<EventSuggestion> Shown { get; } = [];

    public List<int> Summaries { get; } = [];

    public List<Guid> Removed { get; } = [];

    public Task ShowSuggestionAsync(EventSuggestion suggestion)
    {
        Shown.Add(suggestion);
        return Task.CompletedTask;
    }

    public Task ShowSummaryAsync(int count)
    {
        Summaries.Add(count);
        return Task.CompletedTask;
    }

    public Task ShowStatusAsync(string title, string body) => Task.CompletedTask;

    public Task RemoveAsync(Guid suggestionId)
    {
        Removed.Add(suggestionId);
        return Task.CompletedTask;
    }
}

/// <summary>Never matches. Used when a test is about something other than the blacklist.</summary>
public sealed class NeverMatches : IBlacklistMatcher
{
    public Task<BlacklistMatch?> MatchAsync(ClassificationResult result, CancellationToken ct) =>
        Task.FromResult<BlacklistMatch?>(null);
}

public static class TestData
{
    public static RawEmail Email(
        string entryId = "E1",
        string subject = "Free pizza at the CS Club kickoff",
        string body = "Come to Kemper 1131 on Friday for the CS Club kickoff. Free pizza and soda for everyone!",
        DateTimeOffset? received = null,
        bool autoReply = false,
        string messageClass = "IPM.Note") => new(
            entryId,
            "STORE1",
            subject,
            "Campus Activities Board",
            "cab@university.edu",
            received ?? new DateTimeOffset(2026, 7, 20, 14, 2, 0, TimeSpan.FromHours(-5)),
            messageClass,
            body,
            null,
            autoReply,
            "Inbox");

    public static ClassificationResult FreeFoodEvent(
        string topicTag = "cs-club-pizza-kickoff",
        string category = EventCategory.ClubMeeting,
        string startLocal = "2026-07-24T17:00",
        ConfidenceLevel confidence = ConfidenceLevel.High,
        string reason = "CS Club kickoff meeting offering free pizza to attendees.") => new()
        {
            IsEvent = true,
            HasFreeFood = true,
            Confidence = confidence,
            Title = "CS Club Kickoff",
            FoodDescription = "free pizza and soda",
            Location = "Kemper 1131",
            Organization = "CS Club",
            StartLocal = startLocal,
            EndLocal = "",
            IsAllDay = false,
            DateIsExplicit = true,
            Category = category,
            TopicTag = topicTag,
            Reason = reason,
        };

    public static ClassificationResult NotAnEvent() => new()
    {
        IsEvent = false,
        HasFreeFood = false,
        Confidence = ConfidenceLevel.High,
        Category = EventCategory.Other,
        TopicTag = "",
        Reason = "Administrative notice with no event or food.",
    };
}
