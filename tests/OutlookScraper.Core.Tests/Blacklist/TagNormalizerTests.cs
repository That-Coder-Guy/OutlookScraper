using FluentAssertions;
using OutlookScraper.Core.Blacklist;
using Xunit;

namespace OutlookScraper.Core.Tests.Blacklist;

public sealed class TagNormalizerTests
{
    [Theory]
    // The case the whole design turns on: same concept, different word order.
    [InlineData("free-pizza-club-meeting", "club-meeting-with-free-pizza")]
    [InlineData("club-meeting-pizza", "pizza-club-meeting")]
    // Stop words carry no signal and must not affect the key.
    [InlineData("pizza-for-the-club", "club-pizza")]
    // "free" appears in nearly every tag in this domain, so it separates nothing.
    [InlineData("free-bagel-friday", "bagel-friday")]
    // Plurals fold together.
    [InlineData("club-meetings-snacks", "club-meeting-snack")]
    // Small, high-confidence synonyms.
    [InlineData("frat-rush-pizza", "fraternity-recruitment-pizza")]
    [InlineData("dept-lunch", "department-luncheon")]
    public void NormalizesEquivalentTagsToTheSameKey(string a, string b) =>
        TagNormalizer.Normalize(a).Should().Be(TagNormalizer.Normalize(b));

    [Theory]
    [InlineData("cs-club-pizza", "greek-life-formal")]
    [InlineData("career-fair-snacks", "chemistry-seminar-lunch")]
    public void KeepsGenuinelyDifferentTagsApart(string a, string b) =>
        TagNormalizer.Normalize(a).Should().NotBe(TagNormalizer.Normalize(b));

    [Fact]
    public void ProducesTokensInSortedOrderSoInputOrderIsIrrelevant() =>
        TagNormalizer.Tokenize("pizza-club-meeting")
            .Should().Equal("club", "meeting", "pizza");

    [Fact]
    public void DeduplicatesRepeatedTokens() =>
        TagNormalizer.Tokenize("pizza-pizza-club").Should().Equal("club", "pizza");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // A tag consisting only of stop words normalizes away to nothing, which must not
    // then match every other empty-keyed tag as "exact".
    [InlineData("the-free-event")]
    public void ReturnsEmptyKeyForTagsWithNoSignal(string? tag) =>
        TagNormalizer.Normalize(tag).Should().BeEmpty();

    [Fact]
    public void HandlesArbitrarySeparatorsAndCasing() =>
        TagNormalizer.Normalize("CS_Club  Pizza/Night")
            .Should().Be(TagNormalizer.Normalize("cs-club-pizza-night"));

    [Theory]
    // Conservative stemming: these must not be over-folded into each other.
    [InlineData("class", "clas")]
    [InlineData("business", "busines")]
    public void DoesNotStripDoubleSFromShortWords(string input, string wrongResult) =>
        TagNormalizer.Normalize(input).Should().NotBe(wrongResult);
}
