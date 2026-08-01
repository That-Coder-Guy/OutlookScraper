using FluentAssertions;
using OutlookScraper.Core.Blacklist;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Pipeline;
using OutlookScraper.Core.Settings;
using OutlookScraper.Core.Storage;
using OutlookScraper.Core.Text;
using OutlookScraper.Core.Time;
using Xunit;

namespace OutlookScraper.Core.Tests;

/// <summary>
/// Exercises the real pipeline against a real database, with only the model and the
/// embedding provider faked.
/// </summary>
public sealed class PipelineTests : IDisposable
{
    private readonly Database _database = Database.InMemory();
    private readonly ProcessedMessageRepository _messages;
    private readonly SuggestionRepository _suggestions;
    private readonly BlacklistRepository _blacklist;
    private readonly FakeClassifier _classifier = new();
    private readonly FakeEmbeddingProvider _embeddings = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly AppSettings _settings = new();

    public PipelineTests()
    {
        using (var connection = _database.Open())
        {
            Migrations.Apply(connection);
        }

        _messages = new ProcessedMessageRepository(_database);
        _suggestions = new SuggestionRepository(_database);
        _blacklist = new BlacklistRepository(_database);

        _settings.Calendar.TimeZone = "America/New_York";
    }

    public void Dispose() => _database.Dispose();

    private HybridBlacklistMatcher Matcher() =>
        new(_blacklist, _embeddings, _settings.Blacklist);

    private MailPipeline Create() => new(
        _messages,
        _suggestions,
        _blacklist,
        _classifier,
        Matcher(),
        new EmailPreparer(_settings.Ollama),
        new EventTimeResolver(_settings.Calendar),
        _settings,
        _clock);

    [Fact]
    public async Task CreatesASuggestionForAFreeFoodEvent()
    {
        _classifier.Result = TestData.FreeFoodEvent();

        var outcome = await Create().ProcessAsync(TestData.Email(), CancellationToken.None);

        outcome.Kind.Should().Be(PipelineOutcomeKind.Suggested);
        outcome.Suggestion.Should().NotBeNull();
        outcome.Suggestion!.Title.Should().Be("CS Club Kickoff");
        outcome.Suggestion.TopicTagKey.Should().Be(TagNormalizer.Normalize("cs-club-pizza-kickoff"));

        // Must be persisted before any toast is raised, because the toast payload
        // carries only the id and is resolved from the database.
        (await _suggestions.GetAsync(outcome.Suggestion.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task DropsAMessageItHasAlreadySeen()
    {
        _classifier.Result = TestData.FreeFoodEvent();
        var pipeline = Create();
        var email = TestData.Email();

        await pipeline.ProcessAsync(email, CancellationToken.None);
        var second = await pipeline.ProcessAsync(email, CancellationToken.None);

        second.Kind.Should().Be(PipelineOutcomeKind.Duplicate);
        _classifier.Calls.Should().Be(1, "a duplicate must not pay for the model again");
    }

    [Fact]
    public async Task SkipsNonMailItems()
    {
        var outcome = await Create().ProcessAsync(
            TestData.Email(messageClass: "IPM.Schedule.Meeting.Request"), CancellationToken.None);

        outcome.Kind.Should().Be(PipelineOutcomeKind.Skipped);
        outcome.SkipReason.Should().Be(SkipReason.NotMailItem);
        _classifier.Calls.Should().Be(0);
    }

    [Fact]
    public async Task SkipsAutoReplies()
    {
        var outcome = await Create().ProcessAsync(
            TestData.Email(autoReply: true), CancellationToken.None);

        outcome.SkipReason.Should().Be(SkipReason.AutoReply);
        _classifier.Calls.Should().Be(0);
    }

    [Fact]
    public async Task SkipsMessagesWithNothingInThem()
    {
        var outcome = await Create().ProcessAsync(
            TestData.Email(subject: "re: ok", body: "thanks"), CancellationToken.None);

        outcome.SkipReason.Should().Be(SkipReason.BodyTooShort);
        _classifier.Calls.Should().Be(0);
    }

    /// <summary>
    /// The whole announcement can live in the subject line — "Free pizza at the
    /// engineering building" with a two-word body is a real email, and the prompt
    /// includes the subject, so gating on body length alone discarded classifiable mail
    /// before the model ever saw it.
    /// </summary>
    [Fact]
    public async Task ClassifiesAShortBodyWhenTheSubjectCarriesTheAnnouncement()
    {
        _classifier.Result = TestData.FreeFoodEvent();

        var outcome = await Create().ProcessAsync(
            TestData.Email(
                subject: "Free pizza at the engineering building",
                body: "Come by!"),
            CancellationToken.None);

        _classifier.Calls.Should().Be(1);
        outcome.Kind.Should().Be(PipelineOutcomeKind.Suggested);
    }

    [Fact]
    public async Task ReusesAPriorVerdictForAResendWithANewEntryId()
    {
        _classifier.Result = TestData.FreeFoodEvent();
        var pipeline = Create();

        await pipeline.ProcessAsync(TestData.Email(entryId: "A"), CancellationToken.None);
        var second = await pipeline.ProcessAsync(TestData.Email(entryId: "B"), CancellationToken.None);

        second.SkipReason.Should().Be(SkipReason.DuplicateBody);
        _classifier.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DoesNotSuggestWhenTheModelSaysItIsNotAnEvent()
    {
        _classifier.Result = TestData.NotAnEvent();

        var outcome = await Create().ProcessAsync(TestData.Email(), CancellationToken.None);

        outcome.Kind.Should().Be(PipelineOutcomeKind.NotRelevant);
        (await _suggestions.CountByStateAsync(SuggestionState.Pending)).Should().Be(0);
    }

    [Fact]
    public async Task DoesNotSuggestBelowTheConfidenceThreshold()
    {
        _settings.Ollama.ConfidenceThreshold = 0.9;
        _classifier.Result = TestData.FreeFoodEvent(confidence: ConfidenceLevel.Medium);

        var outcome = await Create().ProcessAsync(TestData.Email(), CancellationToken.None);

        outcome.Kind.Should().Be(PipelineOutcomeKind.NotRelevant);
    }

    [Fact]
    public async Task RecordsTheMessageEvenWhenItIsNotRelevantSoItIsNeverReprocessed()
    {
        _classifier.Result = TestData.NotAnEvent();
        await Create().ProcessAsync(TestData.Email(), CancellationToken.None);

        var stored = await _messages.GetAsync("E1");

        stored!.Status.Should().Be(ProcessedStatus.Classified);
    }

    [Fact]
    public async Task SuppressesAnEventMatchingAnExistingBlacklistRule()
    {
        await _blacklist.UpsertAsync(new BlacklistEntry
        {
            Id = Guid.NewGuid(),
            Category = EventCategory.ClubMeeting,
            TopicTag = "cs-club-pizza-kickoff",
            TopicTagKey = TagNormalizer.Normalize("cs-club-pizza-kickoff"),
            CreatedUtc = _clock.UtcNow,
        });

        _classifier.Result = TestData.FreeFoodEvent();

        var outcome = await Create().ProcessAsync(TestData.Email(), CancellationToken.None);

        outcome.Kind.Should().Be(PipelineOutcomeKind.Suppressed);
        outcome.Suggestion!.SuppressStage.Should().Be(SuppressStage.Exact);

        // Still stored, so it shows up in the Suppressed tab and remains auditable.
        var stored = await _suggestions.GetAsync(outcome.Suggestion.Id);
        stored!.State.Should().Be(SuggestionState.Suppressed);
    }

    [Fact]
    public async Task MarksAMessageFailedAndRetryableWhenTheModelErrors()
    {
        _classifier.Throws = new InvalidOperationException("ollama is down");

        var outcome = await Create().ProcessAsync(TestData.Email(), CancellationToken.None);

        outcome.Kind.Should().Be(PipelineOutcomeKind.Failed);

        var stored = await _messages.GetAsync("E1");
        stored!.Status.Should().Be(ProcessedStatus.Failed);
        stored.Attempts.Should().Be(1);
        stored.IsTerminal.Should().BeFalse("a failed message must remain retryable");
    }

    [Fact]
    public async Task FlagsASuggestionWhoseDateCouldNotBeResolved()
    {
        _classifier.Result = TestData.FreeFoodEvent(startLocal: "");

        var outcome = await Create().ProcessAsync(TestData.Email(), CancellationToken.None);

        // Still surfaced — the user can read the email and fill the date in — but not
        // bookable as-is.
        outcome.Kind.Should().Be(PipelineOutcomeKind.Suggested);
        outcome.Suggestion!.NeedsDateReview.Should().BeTrue();
        outcome.Suggestion.StartUtc.Should().BeNull();
        outcome.Suggestion.CanAddToCalendar.Should().BeFalse();
    }

    [Fact]
    public async Task FlagsASuggestionWhoseDateTheModelAdmittedToGuessing()
    {
        _classifier.Result = TestData.FreeFoodEvent() with { DateIsExplicit = false };

        var outcome = await Create().ProcessAsync(TestData.Email(), CancellationToken.None);

        outcome.Suggestion!.NeedsDateReview.Should().BeTrue();
    }
}

public sealed class BlacklistServiceTests : IDisposable
{
    private readonly Database _database = Database.InMemory();
    private readonly ProcessedMessageRepository _messages;
    private readonly SuggestionRepository _suggestions;
    private readonly BlacklistRepository _blacklist;
    private readonly FakeEmbeddingProvider _embeddings = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly BlacklistSettings _settings = new();

    public BlacklistServiceTests()
    {
        using (var connection = _database.Open())
        {
            Migrations.Apply(connection);
        }

        _messages = new ProcessedMessageRepository(_database);
        _suggestions = new SuggestionRepository(_database);
        _blacklist = new BlacklistRepository(_database);
    }

    public void Dispose() => _database.Dispose();

    private BlacklistService Create() => new(
        _blacklist,
        _suggestions,
        new HybridBlacklistMatcher(_blacklist, _embeddings, _settings),
        _embeddings,
        _clock);

    private async Task<EventSuggestion> SeedSuggestionAsync(string entryId, string topicTag)
    {
        await _messages.TryBeginAsync(TestData.Email(entryId: entryId));

        var suggestion = new EventSuggestion
        {
            Id = Guid.NewGuid(),
            EntryId = entryId,
            Title = topicTag,
            Category = EventCategory.GreekLifeRecruitment,
            TopicTag = topicTag,
            TopicTagKey = TagNormalizer.Normalize(topicTag),
            Reason = "Rush event with free pizza.",
            StartUtc = _clock.UtcNow.AddDays(2),
            EndUtc = _clock.UtcNow.AddDays(2).AddHours(1),
            CreatedUtc = _clock.UtcNow,
        };

        await _suggestions.InsertAsync(suggestion);
        return suggestion;
    }

    [Fact]
    public async Task BlacklistingSweepsMatchingPendingSuggestions()
    {
        var target = await SeedSuggestionAsync("E1", "fraternity-recruitment-pizza");
        await SeedSuggestionAsync("E2", "frat-rush-pizza");
        await SeedSuggestionAsync("E3", "pizza-fraternity-recruitment");
        await SeedSuggestionAsync("E4", "career-fair-snacks");

        var (entry, swept) = await Create().BlacklistAsync(target.Id);

        // Blacklisting one frat-pizza email should clear the others already queued,
        // rather than leaving them to be dismissed one at a time.
        swept.Should().Be(2);
        entry.TopicTag.Should().Be("fraternity-recruitment-pizza");

        (await _suggestions.GetAsync(target.Id))!.State.Should().Be(SuggestionState.Blacklisted);
        (await _suggestions.CountByStateAsync(SuggestionState.Suppressed)).Should().Be(2);
        (await _suggestions.CountByStateAsync(SuggestionState.Pending)).Should().Be(1);
    }

    [Fact]
    public async Task BlacklistingSucceedsWithNoEmbeddingModelInstalled()
    {
        _embeddings.Available = false;
        var target = await SeedSuggestionAsync("E1", "fraternity-recruitment-pizza");

        var (entry, _) = await Create().BlacklistAsync(target.Id);

        // A user action must never be blocked on Ollama being reachable.
        entry.Embedding.Should().BeNull();
        entry.EmbedModel.Should().BeNull();
    }

    [Fact]
    public async Task RemovingARuleRestoresWhatItSuppressed()
    {
        var target = await SeedSuggestionAsync("E1", "fraternity-recruitment-pizza");
        await SeedSuggestionAsync("E2", "frat-rush-pizza");

        var service = Create();
        var (entry, swept) = await service.BlacklistAsync(target.Id);
        swept.Should().Be(1);

        var restored = await service.RemoveAsync(entry.Id);

        restored.Should().Be(1);
        (await _suggestions.CountByStateAsync(SuggestionState.Pending)).Should().Be(1);
        (await _blacklist.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RescuingRecordsAnExceptionSoTheRuleCannotReMatchThatTag()
    {
        var target = await SeedSuggestionAsync("E1", "fraternity-recruitment-pizza");
        var collateral = await SeedSuggestionAsync("E2", "frat-rush-pizza");

        var service = Create();
        var (entry, _) = await service.BlacklistAsync(target.Id);

        await service.RescueAsync(collateral.Id);

        (await _suggestions.GetAsync(collateral.Id))!.State.Should().Be(SuggestionState.Pending);

        var exceptions = await _blacklist.GetExceptionsAsync();
        exceptions.Should().ContainSingle();
        exceptions[0].TagId.Should().Be(entry.Id);

        // And a fresh sweep must not swallow it again.
        var swept = await service.SweepPendingAsync(entry.Id);
        swept.Should().Be(0);
    }

    [Fact]
    public async Task BackfillsEmbeddingsOnceAModelBecomesAvailable()
    {
        _embeddings.Available = false;
        var target = await SeedSuggestionAsync("E1", "fraternity-recruitment-pizza");

        var service = Create();
        var (entry, _) = await service.BlacklistAsync(target.Id);
        entry.Embedding.Should().BeNull();

        _embeddings.Available = true;
        _embeddings.Fallback = [0.5f, 0.5f];

        var updated = await service.BackfillEmbeddingsAsync();

        updated.Should().Be(1);
        (await _blacklist.GetAsync(entry.Id))!.Embedding.Should().Equal(0.5f, 0.5f);
    }

    [Fact]
    public async Task BackfillDoesNothingWhenEmbeddingsAreStillUnavailable()
    {
        _embeddings.Available = false;
        var target = await SeedSuggestionAsync("E1", "fraternity-recruitment-pizza");

        var service = Create();
        await service.BlacklistAsync(target.Id);

        (await service.BackfillEmbeddingsAsync()).Should().Be(0);
    }
}
