using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Ollama;
using OutlookScraper.Core.Settings;
using Xunit;

namespace OutlookScraper.Core.Tests.Ollama;

/// <summary>Captures outgoing requests and replays canned responses.</summary>
internal sealed class StubHandler(params string[] responses) : HttpMessageHandler
{
    private readonly Queue<string> _responses = new(responses);

    public List<string> RequestBodies { get; } = [];

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }

        var body = _responses.Count > 0 ? _responses.Dequeue() : "{}";

        return new HttpResponseMessage(StatusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}

public sealed class OllamaClientTests
{
    private static string ChatResponse(string content) =>
        JsonSerializer.Serialize(new { message = new { role = "assistant", content } });

    private static (OllamaClient Client, StubHandler Handler) Create(params string[] responses)
    {
        var handler = new StubHandler(responses);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") };
        return (new OllamaClient(http), handler);
    }

    [Fact]
    public async Task SendsTheDocumentedChatRequestShape()
    {
        var (client, handler) = Create(ChatResponse("{}"));

        await client.ChatAsync(
            "llama3.1:8b", "system", "user", ClassificationSchema.Instance, "10m", 8192,
            CancellationToken.None);

        using var request = JsonDocument.Parse(handler.RequestBodies.Single());
        var root = request.RootElement;

        root.GetProperty("model").GetString().Should().Be("llama3.1:8b");
        root.GetProperty("stream").GetBoolean().Should().BeFalse();
        root.GetProperty("keep_alive").GetString().Should().Be("10m");
        root.GetProperty("options").GetProperty("temperature").GetInt32().Should().Be(0);
        root.GetProperty("options").GetProperty("num_ctx").GetInt32().Should().Be(8192);
        root.TryGetProperty("format", out var format).Should().BeTrue();
        format.GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public async Task ReusesTheCachedSchemaAcrossRequests()
    {
        // The schema node is cached and shared; sending it twice must not throw
        // (a JsonNode cannot be parented twice without cloning).
        var (client, _) = Create(ChatResponse("{}"), ChatResponse("{}"));

        var send = async () =>
        {
            await client.ChatAsync("m", "s", "u", ClassificationSchema.Instance, "10m", 8192, default);
            await client.ChatAsync("m", "s", "u", ClassificationSchema.Instance, "10m", 8192, default);
        };

        await send.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ParsesInstalledModels()
    {
        var payload = JsonSerializer.Serialize(new
        {
            models = new[] { new { name = "llama3.1:8b" }, new { name = "nomic-embed-text:latest" } },
        });

        var (client, _) = Create(payload);

        var models = await client.ListModelsAsync(CancellationToken.None);

        models.Should().BeEquivalentTo("llama3.1:8b", "nomic-embed-text:latest");
    }

    [Fact]
    public async Task ParsesAnEmbeddingResponse()
    {
        var payload = JsonSerializer.Serialize(new { embeddings = new[] { new[] { 0.1f, 0.2f } } });
        var (client, _) = Create(payload);

        var vector = await client.EmbedAsync("nomic-embed-text", "text", CancellationToken.None);

        vector.Should().Equal(0.1f, 0.2f);
    }

    [Fact]
    public async Task ThrowsWhenTheServerErrors()
    {
        var (client, handler) = Create(ChatResponse("{}"));
        handler.StatusCode = HttpStatusCode.InternalServerError;

        var act = async () => await client.ChatAsync(
            "m", "s", "u", null, "10m", 8192, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}

public sealed class ClassificationParsingTests
{
    private static string Valid(string extra = "") => $$"""
        {
          "is_event": true,
          "has_free_food": true,
          "confidence": "high",
          "title": "CS Club Kickoff",
          "food_description": "free pizza",
          "location": "Kemper 1131",
          "organization": "CS Club",
          "start_local": "2026-07-24T17:00",
          "end_local": "",
          "is_all_day": false,
          "date_is_explicit": true,
          "category": "club-meeting",
          "topic_tag": "cs-club-pizza-kickoff",
          "reason": "Club kickoff with free pizza."{{extra}}
        }
        """;

    [Fact]
    public void ParsesAWellFormedResponse()
    {
        OllamaClassifier.TryParse(Valid(), out var result).Should().BeTrue();

        result.IsEvent.Should().BeTrue();
        result.HasFreeFood.Should().BeTrue();
        result.Confidence.Should().Be(ConfidenceLevel.High);
        result.Category.Should().Be(EventCategory.ClubMeeting);
        result.TopicTag.Should().Be("cs-club-pizza-kickoff");
    }

    [Fact]
    public void IgnoresUnexpectedExtraProperties() =>
        OllamaClassifier.TryParse(Valid(", \"surprise\": 42"), out _).Should().BeTrue();

    [Fact]
    public void UnwrapsAMarkdownFencedResponse()
    {
        var fenced = "Here you go:\n```json\n" + Valid() + "\n```";

        OllamaClassifier.TryParse(fenced, out var result).Should().BeTrue();
        result.TopicTag.Should().Be("cs-club-pizza-kickoff");
    }

    [Fact]
    public void CoercesAnUnknownCategoryToOther()
    {
        var json = Valid().Replace("\"club-meeting\"", "\"underwater-basket-weaving\"");

        OllamaClassifier.TryParse(json, out var result).Should().BeTrue();
        result.Category.Should().Be(EventCategory.Other);
    }

    [Fact]
    public void AcceptsBooleansEmittedAsStrings()
    {
        var json = Valid().Replace("\"is_event\": true", "\"is_event\": \"true\"");

        OllamaClassifier.TryParse(json, out var result).Should().BeTrue();
        result.IsEvent.Should().BeTrue();
    }

    [Fact]
    public void DefaultsAnUnrecognisedConfidenceToLow()
    {
        var json = Valid().Replace("\"high\"", "\"extremely certain\"");

        OllamaClassifier.TryParse(json, out var result).Should().BeTrue();
        result.Confidence.Should().Be(ConfidenceLevel.Low);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"is_event\": true")]
    [InlineData("[]")]
    public void RejectsMalformedOutput(string raw) =>
        OllamaClassifier.TryParse(raw, out _).Should().BeFalse();

    [Fact]
    public void RejectsOutputMissingTheDecisionFields()
    {
        // Without is_event and has_free_food there is nothing to act on.
        OllamaClassifier.TryParse("{\"title\": \"Something\"}", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(ConfidenceLevel.High, 0.9)]
    [InlineData(ConfidenceLevel.Medium, 0.6)]
    [InlineData(ConfidenceLevel.Low, 0.3)]
    public void MapsConfidenceOntoTheConfiguredThresholdScale(ConfidenceLevel level, double expected) =>
        level.ToScore().Should().Be(expected);

    [Theory]
    [InlineData(ConfidenceLevel.High, 0.6, true)]
    [InlineData(ConfidenceLevel.Medium, 0.6, true)]
    [InlineData(ConfidenceLevel.Low, 0.6, false)]
    public void GatesOnConfidence(ConfidenceLevel level, double threshold, bool expected) =>
        TestData.FreeFoodEvent(confidence: level)
            .QualifiesAsFreeFoodEvent(threshold).Should().Be(expected);

    [Fact]
    public void DoesNotQualifyWhenFoodIsNotFree()
    {
        var result = TestData.FreeFoodEvent() with { HasFreeFood = false };

        result.QualifiesAsFreeFoodEvent(0.3).Should().BeFalse();
    }
}

public sealed class ClassificationSchemaTests
{
    [Fact]
    public void MarksEveryPropertyRequired()
    {
        var schema = JsonDocument.Parse(ClassificationSchema.ToJson()).RootElement;

        var required = schema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToHashSet();

        var properties = schema.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name).ToHashSet();

        // All-required-with-sentinels is the only shape that reliably round-trips
        // through Ollama's grammar compilation.
        required.Should().BeEquivalentTo(properties);
    }

    [Fact]
    public void ForbidsAdditionalProperties() =>
        JsonDocument.Parse(ClassificationSchema.ToJson()).RootElement
            .GetProperty("additionalProperties").GetBoolean().Should().BeFalse();

    [Fact]
    public void ConstrainsCategoryToTheClosedSet()
    {
        var categories = JsonDocument.Parse(ClassificationSchema.ToJson()).RootElement
            .GetProperty("properties").GetProperty("category").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        categories.Should().BeEquivalentTo(EventCategory.All);
    }

    [Fact]
    public void UsesAnEnumForConfidenceRatherThanANumber()
    {
        var confidence = JsonDocument.Parse(ClassificationSchema.ToJson()).RootElement
            .GetProperty("properties").GetProperty("confidence");

        confidence.GetProperty("type").GetString().Should().Be("string");
        confidence.GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo("low", "medium", "high");
    }

    [Fact]
    public void DoesNotConstrainDatetimesWithARegexPattern()
    {
        // Grammar-level regex support is unreliable; datetimes are validated in C#.
        var start = JsonDocument.Parse(ClassificationSchema.ToJson()).RootElement
            .GetProperty("properties").GetProperty("start_local");

        start.TryGetProperty("pattern", out _).Should().BeFalse();
    }

    [Fact]
    public void TellsTheModelToTagTheRecurringTypeNotTheInstance()
    {
        var description = JsonDocument.Parse(ClassificationSchema.ToJson()).RootElement
            .GetProperty("properties").GetProperty("topic_tag")
            .GetProperty("description").GetString();

        description.Should().Contain("RECURRING TYPE");
        description.Should().Contain("No dates");
    }
}

public sealed class PromptBuilderTests
{
    [Fact]
    public void AnchorsRelativeDatesAgainstTheReceivedTime()
    {
        var builder = new PromptBuilder("America/New_York");
        var email = new EmailPreparerFixture().Cleaned;

        var message = builder.BuildUserMessage(
            email, new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));

        message.Should().Contain("This email was received on");
        message.Should().Contain("RECEIVED time");
        message.Should().Contain("Monday, 20 July 2026");
    }

    [Fact]
    public void IncludesSenderAndSubject()
    {
        var builder = new PromptBuilder("America/New_York");
        var message = builder.BuildUserMessage(
            new EmailPreparerFixture().Cleaned, DateTimeOffset.UtcNow);

        message.Should().Contain("cab@university.edu");
        message.Should().Contain("Free pizza at the CS Club kickoff");
    }

    [Fact]
    public void SystemPromptExcludesPaidAndDiscountedFood()
    {
        // Matched as individual words so the assertion does not break when the prompt
        // is re-wrapped.
        PromptBuilder.SystemPrompt.Should().Contain("purchase");
        PromptBuilder.SystemPrompt.Should().Contain("discount");
        PromptBuilder.SystemPrompt.Should().Contain("NOT free");
    }

    private sealed class EmailPreparerFixture
    {
        public CleanedEmail Cleaned { get; } =
            new Core.Text.EmailPreparer(new OllamaSettings()).Prepare(TestData.Email());
    }
}
