using FluentAssertions;
using OutlookScraper.Core.Blacklist;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Settings;
using OutlookScraper.Core.Storage;
using Xunit;

namespace OutlookScraper.Core.Tests.Blacklist;

public sealed class HybridBlacklistMatcherTests : IDisposable
{
    private readonly Database _database = Database.InMemory();
    private readonly BlacklistRepository _repository;
    private readonly FakeEmbeddingProvider _embeddings = new();
    private readonly BlacklistSettings _settings = new();

    public HybridBlacklistMatcherTests()
    {
        using (var connection = _database.Open())
        {
            Migrations.Apply(connection);
        }

        _repository = new BlacklistRepository(_database);
    }

    public void Dispose() => _database.Dispose();

    private HybridBlacklistMatcher CreateMatcher() =>
        new(_repository, _embeddings, _settings);

    private async Task<BlacklistEntry> SeedAsync(
        string topicTag,
        string category = EventCategory.ClubMeeting,
        float[]? embedding = null,
        string reason = "seeded rule")
    {
        var entry = new BlacklistEntry
        {
            Id = Guid.NewGuid(),
            Category = category,
            TopicTag = topicTag,
            TopicTagKey = TagNormalizer.Normalize(topicTag),
            Reason = reason,
            Embedding = embedding,
            EmbedModel = embedding is null ? null : _embeddings.ModelName,
            EmbedDim = embedding?.Length,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        return await _repository.UpsertAsync(entry);
    }

    [Fact]
    public async Task ReturnsNullWhenNothingIsBlacklisted()
    {
        var match = await CreateMatcher().MatchAsync(TestData.FreeFoodEvent(), CancellationToken.None);

        match.Should().BeNull();
    }

    [Fact]
    public async Task Stage1MatchesReorderedTagExactly()
    {
        var entry = await SeedAsync("free-pizza-club-meeting");

        var match = await CreateMatcher().MatchAsync(
            TestData.FreeFoodEvent(topicTag: "club-meeting-with-free-pizza"),
            CancellationToken.None);

        match.Should().NotBeNull();
        match!.TagId.Should().Be(entry.Id);
        match.Stage.Should().Be(SuppressStage.Exact);
        match.Score.Should().Be(1.0);
    }

    [Fact]
    public async Task Stage0CategoryMismatchBlocksAnOtherwiseIdenticalTag()
    {
        await SeedAsync("pizza-social", category: EventCategory.GreekLifeRecruitment);

        var match = await CreateMatcher().MatchAsync(
            TestData.FreeFoodEvent(topicTag: "pizza-social", category: EventCategory.AcademicSeminar),
            CancellationToken.None);

        match.Should().BeNull("a rule in one category must never suppress another category");
    }

    [Fact]
    public async Task Stage2MatchesOnPartialTokenOverlap()
    {
        var entry = await SeedAsync("cs-club-pizza-night");

        var match = await CreateMatcher().MatchAsync(
            TestData.FreeFoodEvent(topicTag: "cs-club-pizza"),
            CancellationToken.None);

        match.Should().NotBeNull();
        match!.TagId.Should().Be(entry.Id);
        match.Stage.Should().Be(SuppressStage.Tokens);
        match.Score.Should().BeApproximately(0.75, 0.001);
    }

    [Fact]
    public async Task Stage2IgnoresOverlapBelowTheThreshold()
    {
        await SeedAsync("chemistry-seminar-lunch-series");

        var match = await CreateMatcher().MatchAsync(
            TestData.FreeFoodEvent(topicTag: "lunch"),
            CancellationToken.None);

        match.Should().BeNull();
    }

    [Fact]
    public async Task Stage3SuppressesStronglyOnHighCosine()
    {
        var entry = await SeedAsync("boba-social", embedding: [1f, 0f, 0f]);

        var incoming = TestData.FreeFoodEvent(topicTag: "bubble-tea-mixer");
        // 0.995 cosine — comfortably above the strong threshold.
        _embeddings.Register(incoming.EmbeddingInput(), [0.995f, 0.0999f, 0f]);

        var match = await CreateMatcher().MatchAsync(incoming, CancellationToken.None);

        match.Should().NotBeNull();
        match!.TagId.Should().Be(entry.Id);
        match.Stage.Should().Be(SuppressStage.SemanticStrong);
        match.IsSoft.Should().BeFalse();
    }

    [Fact]
    public async Task Stage3MarksTheSoftBandAsRecoverable()
    {
        await SeedAsync("boba-social", embedding: [1f, 0f, 0f]);

        var incoming = TestData.FreeFoodEvent(topicTag: "tea-tasting-hour");
        // ~0.86 cosine — inside the soft band, so hidden but rescuable.
        _embeddings.Register(incoming.EmbeddingInput(), [0.86f, 0.51f, 0f]);

        var match = await CreateMatcher().MatchAsync(incoming, CancellationToken.None);

        match.Should().NotBeNull();
        match!.Stage.Should().Be(SuppressStage.SemanticSoft);
        match.IsSoft.Should().BeTrue();
    }

    [Theory]
    // Exactly at the strong threshold counts as strong; just below falls to soft.
    [InlineData(0.90, SuppressStage.SemanticStrong)]
    [InlineData(0.899, SuppressStage.SemanticSoft)]
    // Exactly at the soft floor still suppresses.
    [InlineData(0.82, SuppressStage.SemanticSoft)]
    public async Task Stage3HonoursTheExactThresholdBoundaries(double cosine, SuppressStage expected)
    {
        await SeedAsync("boba-social", embedding: [1f, 0f]);

        var incoming = TestData.FreeFoodEvent(topicTag: "unrelated-sounding-tag");
        _embeddings.Register(incoming.EmbeddingInput(), UnitVectorAtCosine(cosine));

        var match = await CreateMatcher().MatchAsync(incoming, CancellationToken.None);

        match.Should().NotBeNull();
        match!.Stage.Should().Be(expected);
    }

    [Fact]
    public async Task Stage3IgnoresCosineBelowTheSoftFloor()
    {
        await SeedAsync("boba-social", embedding: [1f, 0f]);

        var incoming = TestData.FreeFoodEvent(topicTag: "unrelated-sounding-tag");
        _embeddings.Register(incoming.EmbeddingInput(), UnitVectorAtCosine(0.819));

        var match = await CreateMatcher().MatchAsync(incoming, CancellationToken.None);

        match.Should().BeNull();
    }

    [Fact]
    public async Task FallsBackToStages0Through2WhenEmbeddingsAreUnavailable()
    {
        _embeddings.Available = false;
        var entry = await SeedAsync("free-pizza-club-meeting");

        var matcher = CreateMatcher();

        // Stage 1 still works with no embedding model at all.
        var exact = await matcher.MatchAsync(
            TestData.FreeFoodEvent(topicTag: "club-meeting-free-pizza"), CancellationToken.None);

        exact.Should().NotBeNull();
        exact!.TagId.Should().Be(entry.Id);

        // Stage 3 simply does not run, so a semantic-only similarity is not caught.
        var semantic = await matcher.MatchAsync(
            TestData.FreeFoodEvent(topicTag: "totally-different-wording"), CancellationToken.None);

        semantic.Should().BeNull();
    }

    [Fact]
    public async Task SkipsEmbeddingsProducedByADifferentModel()
    {
        // A vector from another model is not comparable and must not be used.
        var entry = new BlacklistEntry
        {
            Id = Guid.NewGuid(),
            Category = EventCategory.ClubMeeting,
            TopicTag = "boba-social",
            TopicTagKey = TagNormalizer.Normalize("boba-social"),
            Embedding = [1f, 0f],
            EmbedModel = "some-other-model",
            EmbedDim = 2,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        await _repository.UpsertAsync(entry);

        var incoming = TestData.FreeFoodEvent(topicTag: "unrelated-sounding-tag");
        _embeddings.Register(incoming.EmbeddingInput(), [1f, 0f]);

        var match = await CreateMatcher().MatchAsync(incoming, CancellationToken.None);

        match.Should().BeNull();
    }

    [Fact]
    public async Task HonoursAUserRescueSoTheSameRuleCannotReMatchThatTag()
    {
        var entry = await SeedAsync("free-pizza-club-meeting");
        var incoming = TestData.FreeFoodEvent(topicTag: "club-meeting-free-pizza");

        await _repository.AddExceptionAsync(
            entry.Id, TagNormalizer.Normalize(incoming.TopicTag), DateTimeOffset.UtcNow);

        var match = await CreateMatcher().MatchAsync(incoming, CancellationToken.None);

        match.Should().BeNull();
    }

    [Fact]
    public async Task IgnoresDisabledRules()
    {
        var entry = await SeedAsync("free-pizza-club-meeting");
        await _repository.SetEnabledAsync(entry.Id, false);

        var match = await CreateMatcher().MatchAsync(
            TestData.FreeFoodEvent(topicTag: "club-meeting-free-pizza"), CancellationToken.None);

        match.Should().BeNull();
    }

    [Fact]
    public async Task DoesNotTreatTwoSignalFreeTagsAsAnExactMatch()
    {
        // Both normalize to an empty key; that must not count as "identical".
        await SeedAsync("the-free-event");

        var match = await CreateMatcher().MatchAsync(
            TestData.FreeFoodEvent(topicTag: "a-free-event"), CancellationToken.None);

        match.Should().BeNull();
    }

    /// <summary>Builds a unit vector whose cosine against [1,0] is exactly <paramref name="cosine"/>.</summary>
    private static float[] UnitVectorAtCosine(double cosine) =>
        [(float)cosine, (float)Math.Sqrt(1 - (cosine * cosine))];
}
