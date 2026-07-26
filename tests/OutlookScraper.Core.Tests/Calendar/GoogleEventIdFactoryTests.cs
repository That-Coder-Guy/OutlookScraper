using FluentAssertions;
using OutlookScraper.Core.Calendar;
using Xunit;

namespace OutlookScraper.Core.Tests.Calendar;

public sealed class GoogleEventIdFactoryTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 24, 17, 0, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void ProducesTheSameIdForTheSameInputs()
    {
        var first = GoogleEventIdFactory.FromEntryId("ENTRY-1", Start);
        var second = GoogleEventIdFactory.FromEntryId("ENTRY-1", Start);

        // This is the whole point: a re-add after losing the local database must collide
        // with the existing event rather than double-booking.
        second.Should().Be(first);
    }

    [Fact]
    public void ProducesDifferentIdsForDifferentMessages() =>
        GoogleEventIdFactory.FromEntryId("ENTRY-1", Start)
            .Should().NotBe(GoogleEventIdFactory.FromEntryId("ENTRY-2", Start));

    [Fact]
    public void ProducesADifferentIdWhenTheStartTimeChanges()
    {
        // Correcting a wrong date is a genuinely different booking and should get its
        // own event rather than colliding with the bad one.
        GoogleEventIdFactory.FromEntryId("ENTRY-1", Start)
            .Should().NotBe(GoogleEventIdFactory.FromEntryId("ENTRY-1", Start.AddDays(1)));
    }

    [Fact]
    public void TreatsEqualInstantsInDifferentZonesAsTheSameEvent()
    {
        var utc = Start.ToUniversalTime();

        GoogleEventIdFactory.FromEntryId("ENTRY-1", utc)
            .Should().Be(GoogleEventIdFactory.FromEntryId("ENTRY-1", Start));
    }

    [Theory]
    [InlineData("ENTRY-1")]
    [InlineData("00000000C6A1B2")]
    [InlineData("")]
    [InlineData("unicode-éè-entry")]
    public void ProducesIdsGoogleWillAccept(string entryId)
    {
        var id = GoogleEventIdFactory.FromEntryId(entryId, Start);

        GoogleEventIdFactory.IsValid(id).Should().BeTrue();
        id.Length.Should().BeInRange(5, 1024);
        // base32hex only: a-v and 0-9.
        id.Should().MatchRegex("^[a-v0-9]+$");
    }

    [Theory]
    [InlineData("abc")]              // too short
    [InlineData("has-a-hyphen")]     // hyphen is outside the alphabet
    [InlineData("UPPERCASE")]        // must be lowercase
    [InlineData("includesw")]        // w, x, y, z are outside base32hex
    [InlineData(null)]
    public void RejectsIdsGoogleWouldNot(string? id) =>
        GoogleEventIdFactory.IsValid(id).Should().BeFalse();
}
