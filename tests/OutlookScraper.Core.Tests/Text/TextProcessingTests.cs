using FluentAssertions;
using OutlookScraper.Core.Settings;
using OutlookScraper.Core.Text;
using Xunit;

namespace OutlookScraper.Core.Tests.Text;

public sealed class TruncatorTests
{
    [Fact]
    public void LeavesShortTextAlone() =>
        Truncator.HeadAndTail("short", 100, 50).Should().Be("short");

    [Fact]
    public void LeavesTextExactlyAtTheBoundaryAlone()
    {
        var text = new string('a', 150);

        Truncator.HeadAndTail(text, 100, 50).Should().Be(text);
    }

    [Fact]
    public void KeepsBothEndsOnceOverTheBoundary()
    {
        var text = new string('a', 100) + new string('b', 500) + new string('c', 50);

        var result = Truncator.HeadAndTail(text, 100, 50);

        result.Should().StartWith(new string('a', 100));
        result.Should().EndWith(new string('c', 50));
        result.Should().Contain(Truncator.Marker);
    }

    [Fact]
    public void KeepsTheTailBecauseEventLogisticsOftenSitAtTheBottom()
    {
        var body = "Newsletter intro. " + new string('x', 5000) +
                   "\nWhen: Friday 5pm. Where: Kemper 1131. Free pizza!";

        var result = Truncator.HeadAndTail(body, 4000, 800);

        result.Should().Contain("Free pizza!");
        result.Should().Contain("Kemper 1131");
    }

    [Fact]
    public void SupportsHeadOnlyTruncation()
    {
        var result = Truncator.HeadAndTail(new string('a', 500), 100, 0);

        result.Should().Be(new string('a', 100) + Truncator.Marker);
    }
}

public sealed class EmailBodyCleanerTests
{
    [Fact]
    public void StripsAQuotedReplyChain()
    {
        var body = """
            Reminder: free bagels tomorrow at 9am in the atrium.

            -----Original Message-----
            From: someone@university.edu
            Subject: something else entirely
            Please ignore all of this quoted content.
            """;

        var cleaned = EmailBodyCleaner.Clean(body);

        cleaned.Should().Contain("free bagels");
        cleaned.Should().NotContain("ignore all of this");
    }

    [Fact]
    public void StripsAngleBracketQuotedLines()
    {
        var body = """
            Yes, there will be snacks.

            > Will there be snacks at the meeting?
            > Asking for a friend.
            """;

        var cleaned = EmailBodyCleaner.Clean(body);

        cleaned.Should().Contain("there will be snacks");
        cleaned.Should().NotContain("Asking for a friend");
    }

    [Fact]
    public void StopsAtAWroteAttribution()
    {
        var body = """
            Free coffee in the lounge this afternoon.

            On Tue, Jul 21, 2026 at 4:15 PM Jane Doe wrote:
            Some earlier message text.
            """;

        var cleaned = EmailBodyCleaner.Clean(body);

        cleaned.Should().Contain("Free coffee");
        cleaned.Should().NotContain("Some earlier message text");
    }

    [Fact]
    public void CutsUnsubscribeBoilerplate()
    {
        var body = """
            Pizza party Friday at 6pm in the union.

            To unsubscribe from this list, click the link below.
            Manage your subscription preferences here.
            """;

        var cleaned = EmailBodyCleaner.Clean(body);

        cleaned.Should().Contain("Pizza party");
        cleaned.Should().NotContain("unsubscribe");
    }

    [Fact]
    public void DoesNotCutOnFooterPhrasesAppearingMidSentence()
    {
        // The marker only counts at the start of a line — otherwise ordinary prose gets
        // truncated.
        var body = "We will tell you how to unsubscribe at the event, which has free tacos.";

        EmailBodyCleaner.Clean(body).Should().Contain("free tacos");
    }

    [Fact]
    public void ReplacesLongTrackingUrlsWithTheirDomain()
    {
        var body = "RSVP at https://tracking.example.edu/r/abc123?utm_source=x&utm_campaign=y for free food.";

        var cleaned = EmailBodyCleaner.Clean(body);

        cleaned.Should().Contain("[link:tracking.example.edu]");
        cleaned.Should().NotContain("utm_campaign");
        cleaned.Should().Contain("free food");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void HandlesEmptyInput(string? body) =>
        EmailBodyCleaner.Clean(body).Should().BeEmpty();
}

public sealed class HtmlToTextTests
{
    [Fact]
    public void ExtractsVisibleTextAndDropsScriptAndStyle()
    {
        var html = """
            <html><head><style>.x{color:red}</style><title>ignored</title></head>
            <body><p>Free donuts</p><script>alert('no')</script><div>Room 200</div></body></html>
            """;

        var text = HtmlToText.Convert(html);

        text.Should().Contain("Free donuts");
        text.Should().Contain("Room 200");
        text.Should().NotContain("alert");
        text.Should().NotContain("color:red");
    }

    [Fact]
    public void DecodesEntities() =>
        HtmlToText.Convert("<p>Pizza &amp; soda &mdash; free!</p>")
            .Should().Contain("Pizza & soda");

    [Fact]
    public void SeparatesBlockElementsSoWordsDoNotRunTogether() =>
        HtmlToText.Convert("<div>Friday</div><div>5pm</div>")
            .Should().NotContain("Friday5pm");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void HandlesEmptyInput(string? html) =>
        HtmlToText.Convert(html).Should().BeEmpty();
}

public sealed class EmailPreparerTests
{
    private static EmailPreparer Create(int head = 4000, int tail = 800) =>
        new(new OllamaSettings { MaxBodyChars = head, TailBodyChars = tail });

    [Fact]
    public void PrefersThePlainTextBody()
    {
        var email = TestData.Email(body: "Plain text with free pizza.") with
        {
            HtmlBody = "<p>HTML version</p>",
        };

        Create().Prepare(email).Body.Should().Contain("Plain text");
    }

    [Fact]
    public void FallsBackToHtmlWhenThePlainBodyIsEmpty()
    {
        var email = TestData.Email(body: "  ") with
        {
            HtmlBody = "<p>Free churros in the quad at noon.</p>",
        };

        Create().Prepare(email).Body.Should().Contain("Free churros");
    }

    [Fact]
    public void ProducesTheSameHashForIdenticalContent()
    {
        var preparer = Create();

        var first = preparer.Prepare(TestData.Email(entryId: "A"));
        var second = preparer.Prepare(TestData.Email(entryId: "B"));

        second.BodyHash.Should().Be(first.BodyHash, "a resend must be recognised as a duplicate");
    }

    [Fact]
    public void ProducesDifferentHashesForDifferentContent()
    {
        var preparer = Create();

        var first = preparer.Prepare(TestData.Email(body: "Free pizza Friday."));
        var second = preparer.Prepare(TestData.Email(body: "Free tacos Tuesday."));

        second.BodyHash.Should().NotBe(first.BodyHash);
    }

    [Fact]
    public void HashesTheUntruncatedBodySoTruncationCannotCauseFalseDuplicates()
    {
        var preparer = Create(head: 20, tail: 5);

        var shared = new string('x', 100);
        var first = preparer.Prepare(TestData.Email(body: shared + " ending one"));
        var second = preparer.Prepare(TestData.Email(body: shared + " ending two"));

        second.BodyHash.Should().NotBe(first.BodyHash);
    }
}
