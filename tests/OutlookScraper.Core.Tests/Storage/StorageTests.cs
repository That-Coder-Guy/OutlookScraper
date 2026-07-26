using FluentAssertions;
using OutlookScraper.Core.Blacklist;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Storage;
using Xunit;

namespace OutlookScraper.Core.Tests.Storage;

public sealed class MigrationsTests
{
    [Fact]
    public void AppliesFromAnEmptyDatabase()
    {
        using var database = Database.InMemory();
        using var connection = database.Open();

        Migrations.GetVersion(connection).Should().Be(0);
        Migrations.Apply(connection);
        Migrations.GetVersion(connection).Should().Be(Migrations.LatestVersion);
    }

    [Fact]
    public void IsIdempotent()
    {
        using var database = Database.InMemory();
        using var connection = database.Open();

        Migrations.Apply(connection);
        var act = () => Migrations.Apply(connection);

        act.Should().NotThrow();
        Migrations.GetVersion(connection).Should().Be(Migrations.LatestVersion);
    }
}

public sealed class ProcessedMessageRepositoryTests : IDisposable
{
    private readonly Database _database = Database.InMemory();
    private readonly ProcessedMessageRepository _repository;

    public ProcessedMessageRepositoryTests()
    {
        using (var connection = _database.Open())
        {
            Migrations.Apply(connection);
        }

        _repository = new ProcessedMessageRepository(_database);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task ClaimsAMessageOnlyOnce()
    {
        var email = TestData.Email();

        // The three delivery paths (ItemAdd, NewMailEx, sweep) all race here; exactly
        // one must win.
        (await _repository.TryBeginAsync(email)).Should().BeTrue();
        (await _repository.TryBeginAsync(email)).Should().BeFalse();
        (await _repository.TryBeginAsync(email)).Should().BeFalse();
    }

    [Fact]
    public async Task ClaimsDistinctMessagesIndependently()
    {
        (await _repository.TryBeginAsync(TestData.Email(entryId: "A"))).Should().BeTrue();
        (await _repository.TryBeginAsync(TestData.Email(entryId: "B"))).Should().BeTrue();
    }

    [Fact]
    public async Task RoundTripsClassificationState()
    {
        var email = TestData.Email();
        await _repository.TryBeginAsync(email);

        var classifiedAt = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        await _repository.MarkClassifiedAsync(
            email.EntryId, "hash-1", "llama3.1:8b", "{\"ok\":true}", classifiedAt);

        var stored = await _repository.GetAsync(email.EntryId);

        stored.Should().NotBeNull();
        stored!.Status.Should().Be(ProcessedStatus.Classified);
        stored.BodyHash.Should().Be("hash-1");
        stored.ModelName.Should().Be("llama3.1:8b");
        stored.ClassifiedUtc.Should().Be(classifiedAt);
        stored.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public async Task RecordsSkipReasons()
    {
        var email = TestData.Email();
        await _repository.TryBeginAsync(email);
        await _repository.MarkSkippedAsync(email.EntryId, SkipReason.AutoReply);

        var stored = await _repository.GetAsync(email.EntryId);

        stored!.Status.Should().Be(ProcessedStatus.Skipped);
        stored.SkipReason.Should().Be(SkipReason.AutoReply);
    }

    [Fact]
    public async Task CountsAttemptsOnRepeatedFailures()
    {
        var email = TestData.Email();
        await _repository.TryBeginAsync(email);

        await _repository.MarkFailedAsync(email.EntryId, "connection refused");
        await _repository.MarkFailedAsync(email.EntryId, "connection refused");

        var stored = await _repository.GetAsync(email.EntryId);

        stored!.Status.Should().Be(ProcessedStatus.Failed);
        stored.Attempts.Should().Be(2);
        stored.LastError.Should().Be("connection refused");
    }

    [Fact]
    public async Task FindsAPriorClassificationByBodyHash()
    {
        var first = TestData.Email(entryId: "A");
        await _repository.TryBeginAsync(first);
        await _repository.MarkClassifiedAsync(
            first.EntryId, "same-hash", "m", null, DateTimeOffset.UtcNow);

        var match = await _repository.FindClassifiedByBodyHashAsync("same-hash", "B");

        match.Should().NotBeNull();
        match!.EntryId.Should().Be("A");
    }

    [Fact]
    public async Task DoesNotMatchItsOwnBodyHash()
    {
        var email = TestData.Email(entryId: "A");
        await _repository.TryBeginAsync(email);
        await _repository.MarkClassifiedAsync(
            email.EntryId, "same-hash", "m", null, DateTimeOffset.UtcNow);

        var match = await _repository.FindClassifiedByBodyHashAsync("same-hash", "A");

        match.Should().BeNull();
    }

    [Fact]
    public async Task ListsFailedMessagesUnderTheAttemptCap()
    {
        await _repository.TryBeginAsync(TestData.Email(entryId: "A"));
        await _repository.MarkFailedAsync("A", "boom");

        (await _repository.GetFailedEntryIdsAsync(maxAttempts: 3)).Should().Contain("A");
        (await _repository.GetFailedEntryIdsAsync(maxAttempts: 1)).Should().BeEmpty();
    }
}

public sealed class SuggestionRepositoryTests : IDisposable
{
    private readonly Database _database = Database.InMemory();
    private readonly ProcessedMessageRepository _messages;
    private readonly SuggestionRepository _suggestions;
    private readonly BlacklistRepository _blacklist;

    public SuggestionRepositoryTests()
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

    private async Task<EventSuggestion> SeedAsync(string entryId = "E1")
    {
        await _messages.TryBeginAsync(TestData.Email(entryId: entryId));

        var suggestion = new EventSuggestion
        {
            Id = Guid.NewGuid(),
            EntryId = entryId,
            Title = "CS Club Kickoff",
            Location = "Kemper 1131",
            StartUtc = new DateTimeOffset(2026, 7, 24, 21, 0, 0, TimeSpan.Zero),
            EndUtc = new DateTimeOffset(2026, 7, 24, 22, 0, 0, TimeSpan.Zero),
            IanaTimeZone = "America/New_York",
            Category = EventCategory.ClubMeeting,
            TopicTag = "cs-club-pizza",
            TopicTagKey = TagNormalizer.Normalize("cs-club-pizza"),
            Reason = "Club kickoff with free pizza.",
            Confidence = 0.9,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        await _suggestions.InsertAsync(suggestion);
        return suggestion;
    }

    [Fact]
    public async Task RoundTripsASuggestion()
    {
        var seeded = await SeedAsync();

        var stored = await _suggestions.GetAsync(seeded.Id);

        stored.Should().NotBeNull();
        stored!.Title.Should().Be("CS Club Kickoff");
        stored.StartUtc.Should().Be(seeded.StartUtc);
        stored.IanaTimeZone.Should().Be("America/New_York");
        stored.State.Should().Be(SuggestionState.Pending);
        stored.CanAddToCalendar.Should().BeTrue();
    }

    [Fact]
    public async Task RecordsTheSuppressionAuditTrail()
    {
        var suggestion = await SeedAsync();

        var tag = await _blacklist.UpsertAsync(new BlacklistEntry
        {
            Id = Guid.NewGuid(),
            Category = EventCategory.ClubMeeting,
            TopicTag = "cs-club-pizza",
            TopicTagKey = TagNormalizer.Normalize("cs-club-pizza"),
            CreatedUtc = DateTimeOffset.UtcNow,
        });

        var match = new BlacklistMatch(tag.Id, SuppressStage.SemanticSoft, 0.86);
        await _suggestions.SuppressAsync(suggestion.Id, match, DateTimeOffset.UtcNow);

        var stored = await _suggestions.GetAsync(suggestion.Id);

        // Without these fields a mis-tuned threshold would be undiagnosable.
        stored!.State.Should().Be(SuggestionState.Suppressed);
        stored.SuppressedByTagId.Should().Be(tag.Id);
        stored.SuppressStage.Should().Be(SuppressStage.SemanticSoft);
        stored.SuppressScore.Should().BeApproximately(0.86, 0.0001);
        stored.IsSoftSuppressed.Should().BeTrue();
    }

    [Fact]
    public async Task UnsuppressReturnsASuggestionToPending()
    {
        var suggestion = await SeedAsync();

        var tag = await _blacklist.UpsertAsync(new BlacklistEntry
        {
            Id = Guid.NewGuid(),
            Category = EventCategory.ClubMeeting,
            TopicTag = "t",
            TopicTagKey = "t",
            CreatedUtc = DateTimeOffset.UtcNow,
        });

        await _suggestions.SuppressAsync(
            suggestion.Id, new BlacklistMatch(tag.Id, SuppressStage.Exact, 1), DateTimeOffset.UtcNow);

        await _suggestions.UnsuppressAsync(suggestion.Id);

        var stored = await _suggestions.GetAsync(suggestion.Id);

        stored!.State.Should().Be(SuggestionState.Pending);
        stored.SuppressedByTagId.Should().BeNull();
        stored.SuppressStage.Should().Be(SuppressStage.None);
    }

    [Fact]
    public async Task FiltersByState()
    {
        var first = await SeedAsync("E1");
        await SeedAsync("E2");

        await _suggestions.SetStateAsync(first.Id, SuggestionState.Added, DateTimeOffset.UtcNow);

        (await _suggestions.GetByStateAsync(SuggestionState.Pending)).Should().HaveCount(1);
        (await _suggestions.CountByStateAsync(SuggestionState.Added)).Should().Be(1);
    }

    [Fact]
    public async Task RetentionSparesMessagesThatStillHavePendingSuggestions()
    {
        var suggestion = await SeedAsync();

        await _messages.PruneAsync(DateTimeOffset.UtcNow.AddYears(1), DateTimeOffset.UtcNow);

        (await _suggestions.GetAsync(suggestion.Id)).Should().NotBeNull(
            "a suggestion the user has not acted on must survive retention");
    }

    [Fact]
    public async Task CascadesWhenTheSourceMessageIsDeleted()
    {
        var suggestion = await SeedAsync();
        await _suggestions.SetStateAsync(suggestion.Id, SuggestionState.Added, DateTimeOffset.UtcNow);

        await _messages.PruneAsync(DateTimeOffset.UtcNow.AddYears(1), DateTimeOffset.UtcNow);

        (await _suggestions.GetAsync(suggestion.Id)).Should().BeNull();
    }
}

public sealed class BlacklistRepositoryTests : IDisposable
{
    private readonly Database _database = Database.InMemory();
    private readonly BlacklistRepository _repository;

    public BlacklistRepositoryTests()
    {
        using (var connection = _database.Open())
        {
            Migrations.Apply(connection);
        }

        _repository = new BlacklistRepository(_database);
    }

    public void Dispose() => _database.Dispose();

    private static BlacklistEntry Entry(
        string tag = "cs-club-pizza",
        string category = EventCategory.ClubMeeting,
        float[]? embedding = null) => new()
        {
            Id = Guid.NewGuid(),
            Category = category,
            TopicTag = tag,
            TopicTagKey = TagNormalizer.Normalize(tag),
            Reason = "seeded",
            Embedding = embedding,
            EmbedModel = embedding is null ? null : "fake-embed",
            EmbedDim = embedding?.Length,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task RoundTripsAnEmbeddingBlob()
    {
        var vector = new[] { 0.1f, -0.25f, 0.75f };
        var saved = await _repository.UpsertAsync(Entry(embedding: vector));

        var stored = await _repository.GetAsync(saved.Id);

        stored!.Embedding.Should().Equal(vector);
        stored.EmbedDim.Should().Be(3);
        stored.HasUsableEmbedding("fake-embed").Should().BeTrue();
        stored.HasUsableEmbedding("other-model").Should().BeFalse();
    }

    [Fact]
    public async Task StoresANullEmbeddingWhenNoModelWasAvailable()
    {
        var saved = await _repository.UpsertAsync(Entry());

        var stored = await _repository.GetAsync(saved.Id);

        stored!.Embedding.Should().BeNull();
        stored.HasUsableEmbedding("fake-embed").Should().BeFalse();
    }

    [Fact]
    public async Task DeduplicatesOnCategoryAndKey()
    {
        var first = await _repository.UpsertAsync(Entry("free-pizza-club-meeting"));
        var second = await _repository.UpsertAsync(Entry("club-meeting-with-free-pizza"));

        // Same normalized key, so blacklisting it twice is a no-op rather than an error.
        second.Id.Should().Be(first.Id);
        (await _repository.GetAllAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task AllowsTheSameTagInDifferentCategories()
    {
        await _repository.UpsertAsync(Entry("pizza-social", EventCategory.ClubMeeting));
        await _repository.UpsertAsync(Entry("pizza-social", EventCategory.GreekLifeRecruitment));

        (await _repository.GetAllAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task ListsRulesNeedingReEmbeddingAfterAModelChange()
    {
        await _repository.UpsertAsync(Entry("a", embedding: [1f, 0f]));
        await _repository.UpsertAsync(Entry("b"));

        // Vectors from a different model are not comparable and must be redone.
        (await _repository.GetNeedingEmbeddingAsync("fake-embed")).Should().HaveCount(1);
        (await _repository.GetNeedingEmbeddingAsync("new-model")).Should().HaveCount(2);
    }

    [Fact]
    public async Task TracksHitCounts()
    {
        var saved = await _repository.UpsertAsync(Entry());

        await _repository.IncrementHitCountAsync(saved.Id);
        await _repository.IncrementHitCountAsync(saved.Id);

        (await _repository.GetAsync(saved.Id))!.HitCount.Should().Be(2);
    }

    [Fact]
    public async Task ExcludesDisabledRulesFromTheEnabledSet()
    {
        var saved = await _repository.UpsertAsync(Entry());
        await _repository.SetEnabledAsync(saved.Id, false);

        (await _repository.GetEnabledAsync()).Should().BeEmpty();
        (await _repository.GetAllAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task ReEnablesOnReUpsertBecauseTheUserAskedAgain()
    {
        var saved = await _repository.UpsertAsync(Entry());
        await _repository.SetEnabledAsync(saved.Id, false);

        await _repository.UpsertAsync(Entry());

        (await _repository.GetEnabledAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task StoresExceptionsIdempotently()
    {
        var saved = await _repository.UpsertAsync(Entry());

        await _repository.AddExceptionAsync(saved.Id, "some-key", DateTimeOffset.UtcNow);
        await _repository.AddExceptionAsync(saved.Id, "some-key", DateTimeOffset.UtcNow);

        (await _repository.GetExceptionsAsync()).Should().HaveCount(1);
    }
}

public sealed class StateRepositoryTests : IDisposable
{
    private readonly Database _database = Database.InMemory();
    private readonly StateRepository _repository;

    public StateRepositoryTests()
    {
        using (var connection = _database.Open())
        {
            Migrations.Apply(connection);
        }

        _repository = new StateRepository(_database);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task UpsertsValues()
    {
        await _repository.SetAsync("k", "one");
        await _repository.SetAsync("k", "two");

        (await _repository.GetAsync("k")).Should().Be("two");
    }

    [Fact]
    public async Task RoundTripsTimestamps()
    {
        var value = new DateTimeOffset(2026, 7, 26, 17, 35, 0, TimeSpan.Zero);
        await _repository.SetTimestampAsync(StateRepository.LastSweepUtc, value);

        (await _repository.GetTimestampAsync(StateRepository.LastSweepUtc)).Should().Be(value);
    }

    [Fact]
    public async Task ReturnsNullForAMissingTimestamp() =>
        (await _repository.GetTimestampAsync("nope")).Should().BeNull();

    [Fact]
    public async Task RoundTripsFlags()
    {
        await _repository.SetFlagAsync(StateRepository.EmbeddingsAvailable, true);
        (await _repository.GetFlagAsync(StateRepository.EmbeddingsAvailable)).Should().BeTrue();

        await _repository.SetFlagAsync(StateRepository.EmbeddingsAvailable, false);
        (await _repository.GetFlagAsync(StateRepository.EmbeddingsAvailable)).Should().BeFalse();
    }
}
