using FluentAssertions;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Settings;
using OutlookScraper.Core.Time;
using Xunit;

namespace OutlookScraper.Core.Tests.Time;

public sealed class EventTimeResolverTests
{
    private const string NewYork = "America/New_York";

    private static readonly DateTimeOffset Received =
        new(2026, 7, 20, 14, 2, 0, TimeSpan.FromHours(-4));

    private static EventTimeResolver Create(string zone = NewYork, int defaultMinutes = 60) =>
        new(new CalendarSettings { TimeZone = zone, DefaultDurationMinutes = defaultMinutes });

    [Fact]
    public void ResolvesANaiveLocalTimeIntoTheConfiguredZone()
    {
        var outcome = Create().Resolve(
            TestData.FreeFoodEvent(startLocal: "2026-07-24T17:00"), Received);

        outcome.IsResolved.Should().BeTrue();
        outcome.Time!.Start.Offset.Should().Be(TimeSpan.FromHours(-4), "July is daylight time in New York");
        outcome.Time.Start.Hour.Should().Be(17);
        outcome.Time.IanaTimeZone.Should().Be(NewYork);
    }

    [Fact]
    public void DefaultsTheEndToTheConfiguredDuration()
    {
        var outcome = Create(defaultMinutes: 90).Resolve(
            TestData.FreeFoodEvent(startLocal: "2026-07-24T17:00"), Received);

        outcome.Time!.Duration.Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void FallsBackToTheDefaultDurationWhenTheStatedEndPrecedesTheStart()
    {
        var result = TestData.FreeFoodEvent(startLocal: "2026-07-24T17:00") with
        {
            EndLocal = "2026-07-24T16:00",
        };

        var outcome = Create().Resolve(result, Received);

        outcome.Time!.End.Should().BeAfter(outcome.Time.Start);
        outcome.Time.Duration.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void HonoursAnExplicitEndTime()
    {
        var result = TestData.FreeFoodEvent(startLocal: "2026-07-24T17:00") with
        {
            EndLocal = "2026-07-24T19:30",
        };

        Create().Resolve(result, Received).Time!.Duration.Should().Be(TimeSpan.FromMinutes(150));
    }

    [Fact]
    public void PushesForwardThroughTheSpringForwardGapRatherThanThrowing()
    {
        // 2026-03-08 02:30 in New York does not exist; clocks jump 02:00 to 03:00.
        var received = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.FromHours(-5));

        var outcome = Create().Resolve(
            TestData.FreeFoodEvent(startLocal: "2026-03-08T02:30"), received);

        outcome.IsResolved.Should().BeTrue();
        outcome.Time!.Start.Hour.Should().Be(3, "the nonexistent 02:30 shifts forward by the DST delta");
        outcome.Time.Start.Offset.Should().Be(TimeSpan.FromHours(-4));
    }

    [Fact]
    public void PicksTheStandardOffsetForAnAmbiguousFallBackTime()
    {
        // 2026-11-01 01:30 in New York happens twice.
        var received = new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.FromHours(-4));

        var outcome = Create().Resolve(
            TestData.FreeFoodEvent(startLocal: "2026-11-01T01:30"), received);

        outcome.IsResolved.Should().BeTrue();
        outcome.Time!.Start.Offset.Should().Be(
            TimeSpan.FromHours(-5), "the later, standard-time instant is chosen deterministically");
    }

    [Fact]
    public void TreatsADateOnlyValueAsAllDay()
    {
        var outcome = Create().Resolve(
            TestData.FreeFoodEvent(startLocal: "2026-07-24"), Received);

        outcome.IsResolved.Should().BeTrue();
        outcome.Time!.IsAllDay.Should().BeTrue();
        outcome.Time.Duration.Should().Be(TimeSpan.FromDays(1));
    }

    [Theory]
    [InlineData("")]
    public void ReportsAMissingDate(string start) =>
        Create().Resolve(TestData.FreeFoodEvent(startLocal: start), Received)
            .Problem.Should().Be(DateResolutionProblem.Missing);

    [Theory]
    // An offset is explicitly forbidden by the prompt, so it is treated as malformed
    // rather than silently trusted.
    [InlineData("2026-07-24T17:00-04:00")]
    [InlineData("2026-07-24T17:00Z")]
    [InlineData("next Friday")]
    [InlineData("07/24/2026 5pm")]
    [InlineData("2026-13-45T99:99")]
    public void ReportsAnUnparseableDate(string start) =>
        Create().Resolve(TestData.FreeFoodEvent(startLocal: start), Received)
            .Problem.Should().Be(DateResolutionProblem.Unparseable);

    [Fact]
    public void RejectsAnEventWellBeforeTheEmailArrived()
    {
        var outcome = Create().Resolve(
            TestData.FreeFoodEvent(startLocal: "2026-06-01T17:00"), Received);

        outcome.IsResolved.Should().BeFalse();
        outcome.Problem.Should().Be(DateResolutionProblem.InPast);
    }

    [Fact]
    public void AllowsAnEventEarlierOnTheDayTheEmailArrived()
    {
        // Same-day mail about an event a few hours earlier is common and should not be
        // discarded as "in the past".
        var outcome = Create().Resolve(
            TestData.FreeFoodEvent(startLocal: "2026-07-20T09:00"), Received);

        outcome.IsResolved.Should().BeTrue();
    }

    [Fact]
    public void RejectsADateBeyondTheLookaheadWindow()
    {
        var outcome = Create().Resolve(
            TestData.FreeFoodEvent(startLocal: "2027-06-01T17:00"), Received);

        outcome.Problem.Should().Be(DateResolutionProblem.TooFarOut);
    }

    [Fact]
    public void JudgesOldMailAgainstItsOwnReceivedTimeNotTheCurrentDate()
    {
        // A backfill legitimately processes months-old mail; judging it against "now"
        // would reject every historical message.
        var oldReceived = new DateTimeOffset(2024, 3, 1, 9, 0, 0, TimeSpan.FromHours(-5));

        var outcome = Create().Resolve(
            TestData.FreeFoodEvent(startLocal: "2024-03-05T12:00"), oldReceived);

        outcome.IsResolved.Should().BeTrue();
    }

    [Fact]
    public void FallsBackToTheMachineZoneWhenTheConfiguredZoneIsUnknown()
    {
        var outcome = Create(zone: "Not/AZone").Resolve(
            TestData.FreeFoodEvent(startLocal: "2026-07-24T17:00"), Received);

        outcome.IsResolved.Should().BeTrue();
    }

    [Theory]
    [InlineData("2026-07-24T17:00", false)]
    [InlineData("2026-07-24T17:00:30", false)]
    [InlineData("2026-07-24 17:00", false)]
    [InlineData("2026-07-24", true)]
    public void AcceptsTheDocumentedInputFormats(string input, bool expectedDateOnly)
    {
        EventTimeResolver.TryParseNaive(input, out _, out var dateOnly).Should().BeTrue();
        dateOnly.Should().Be(expectedDateOnly);
    }
}
