using FluentAssertions;
using OutlookScraper.Core.Models;
using OutlookScraper.Core.Scheduling;
using OutlookScraper.Core.Settings;
using Xunit;

namespace OutlookScraper.Core.Tests.Scheduling;

public sealed class ClassificationQueueTests
{
    [Fact]
    public async Task DrainsLiveMailBeforeBackfill()
    {
        var queue = new ClassificationQueue();

        await queue.EnqueueBackfillAsync("old-1");
        await queue.EnqueueBackfillAsync("old-2");
        await queue.EnqueueLiveAsync("new-1");

        // A large first-run sweep must never delay an email that just arrived.
        var first = await queue.DequeueAsync(CancellationToken.None);

        first!.EntryId.Should().Be("new-1");
        first.Priority.Should().Be(ProcessingPriority.Live);
    }

    [Fact]
    public async Task FallsThroughToBackfillWhenNoLiveMailIsWaiting()
    {
        var queue = new ClassificationQueue();
        await queue.EnqueueBackfillAsync("old-1");

        var item = await queue.DequeueAsync(CancellationToken.None);

        item!.EntryId.Should().Be("old-1");
        item.Priority.Should().Be(ProcessingPriority.Backfill);
    }

    [Fact]
    public async Task PreservesOrderWithinAPriority()
    {
        var queue = new ClassificationQueue();

        await queue.EnqueueLiveAsync("a");
        await queue.EnqueueLiveAsync("b");

        (await queue.DequeueAsync(CancellationToken.None))!.EntryId.Should().Be("a");
        (await queue.DequeueAsync(CancellationToken.None))!.EntryId.Should().Be("b");
    }

    [Fact]
    public async Task ReportsPendingCounts()
    {
        var queue = new ClassificationQueue();

        await queue.EnqueueLiveAsync("a");
        await queue.EnqueueBackfillAsync("b");
        await queue.EnqueueBackfillAsync("c");

        queue.PendingLive.Should().Be(1);
        queue.PendingBackfill.Should().Be(2);
        queue.PendingTotal.Should().Be(3);
    }

    [Fact]
    public async Task BlocksTheProducerWhenTheQueueIsFull()
    {
        // Bounded with FullMode.Wait: the producer is the Outlook sweep, and making it
        // wait is right. Dropping would silently lose mail.
        var queue = new ClassificationQueue(liveCapacity: 1);
        await queue.EnqueueLiveAsync("a");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var act = async () => await queue.EnqueueLiveAsync("b", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WakesUpWhenAnItemArrivesLater()
    {
        var queue = new ClassificationQueue();
        var dequeue = queue.DequeueAsync(CancellationToken.None);

        await Task.Delay(50);
        await queue.EnqueueLiveAsync("late");

        (await dequeue)!.EntryId.Should().Be("late");
    }

    [Fact]
    public async Task ReturnsNullOnCancellation()
    {
        var queue = new ClassificationQueue();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        (await queue.DequeueAsync(cts.Token)).Should().BeNull();
    }
}

public sealed class CircuitBreakerTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartsClosed()
    {
        var breaker = new CircuitBreaker(new FakeClock(Start));

        breaker.State.Should().Be(CircuitState.Closed);
        breaker.CanExecute.Should().BeTrue();
    }

    [Fact]
    public void OpensOnlyAfterTheFailureThreshold()
    {
        var breaker = new CircuitBreaker(new FakeClock(Start), failureThreshold: 3);

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.State.Should().Be(CircuitState.Closed);

        breaker.RecordFailure();
        breaker.State.Should().Be(CircuitState.Open);
        breaker.CanExecute.Should().BeFalse();
    }

    [Fact]
    public void ASuccessResetsTheFailureRun()
    {
        var breaker = new CircuitBreaker(new FakeClock(Start), failureThreshold: 3);

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordSuccess();
        breaker.RecordFailure();
        breaker.RecordFailure();

        breaker.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void MovesToHalfOpenAfterTheResetWindow()
    {
        var clock = new FakeClock(Start);
        var breaker = new CircuitBreaker(clock, failureThreshold: 1, resetAfter: TimeSpan.FromSeconds(60));

        breaker.RecordFailure();
        breaker.State.Should().Be(CircuitState.Open);

        clock.Advance(TimeSpan.FromSeconds(61));

        breaker.State.Should().Be(CircuitState.HalfOpen);
        breaker.CanExecute.Should().BeTrue("one probe is allowed through");
    }

    [Fact]
    public void ClosesWhenTheHalfOpenProbeSucceeds()
    {
        var clock = new FakeClock(Start);
        var breaker = new CircuitBreaker(clock, failureThreshold: 1, resetAfter: TimeSpan.FromSeconds(60));

        breaker.RecordFailure();
        clock.Advance(TimeSpan.FromSeconds(61));
        _ = breaker.State;
        breaker.RecordSuccess();

        breaker.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void ReopensImmediatelyWhenTheHalfOpenProbeFails()
    {
        var clock = new FakeClock(Start);
        var breaker = new CircuitBreaker(clock, failureThreshold: 3, resetAfter: TimeSpan.FromSeconds(60));

        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        clock.Advance(TimeSpan.FromSeconds(61));
        _ = breaker.State;

        // Must not need to re-accumulate the whole threshold.
        breaker.RecordFailure();

        breaker.State.Should().Be(CircuitState.Open);
    }
}

public sealed class ToastRateLimiterTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static GeneralSettings Settings(int max = 3, int windowMinutes = 10) =>
        new() { MaxToastsPerWindow = max, ToastWindowMinutes = windowMinutes };

    [Fact]
    public void AllowsUpToTheBudget()
    {
        var limiter = new ToastRateLimiter(Settings(), new FakeClock(Start));

        limiter.TryClaim().Should().BeTrue();
        limiter.TryClaim().Should().BeTrue();
        limiter.TryClaim().Should().BeTrue();
        limiter.TryClaim().Should().BeFalse();
    }

    [Fact]
    public void CountsWithheldDetectionsForTheSummary()
    {
        var limiter = new ToastRateLimiter(Settings(max: 1), new FakeClock(Start));

        limiter.TryClaim();
        limiter.TryClaim();
        limiter.TryClaim();

        limiter.PendingSummaryCount.Should().Be(2);
        limiter.FlushSummaryCount().Should().Be(2);
        limiter.PendingSummaryCount.Should().Be(0, "flushing resets the counter");
    }

    [Fact]
    public void AllowsToastsAgainOnceTheWindowRollsForward()
    {
        var clock = new FakeClock(Start);
        var limiter = new ToastRateLimiter(Settings(max: 1, windowMinutes: 10), clock);

        limiter.TryClaim().Should().BeTrue();
        limiter.TryClaim().Should().BeFalse();

        clock.Advance(TimeSpan.FromMinutes(11));

        limiter.TryClaim().Should().BeTrue();
    }
}
