# OutlookScraper

A Windows background app that watches your Outlook inbox for campus events with
free food, and offers to put them on your Google Calendar.

It lives in the notification area. When new mail arrives it reads the message
through Outlook's native COM object model, hands it to a **local** Ollama model to
decide whether it describes an event where food is provided at no cost, and — if it
is — shows a toast with two buttons: add it to your calendar, or blacklist that
*kind* of email so similar announcements stop bothering you.

Classification runs entirely on your machine. Nothing leaves it except the event
details you explicitly choose to put on a calendar.

## Requirements

- **Windows 10 build 19041 (2004) or newer.**
- **Classic Outlook desktop**, signed in. The new Outlook client removed COM
  automation entirely, so it will not work — see [Which Outlook?](#which-outlook)
  below.
- **[Ollama](https://ollama.com)** running locally with a model pulled:
  ```
  ollama pull llama3.1:8b        # classification
  ollama pull nomic-embed-text   # optional, improves blacklist matching
  ```
- **.NET 8 desktop runtime** (or publish self-contained).
- A Google account, if you want the calendar half. See `docs/SETUP.md`.

## Getting started

```bash
git clone https://github.com/That-Coder-Guy/OutlookScraper
cd OutlookScraper
dotnet publish src/OutlookScraper.App -c Release -r win-x64 --self-contained false -o publish
publish\OutlookScraper.exe
```

On first run it creates `%LOCALAPPDATA%\OutlookScraper\`, backfills the last week of
mail, and puts an icon in the tray. Right-click it for settings.

Google Calendar needs a one-time setup — follow `docs/SETUP.md`. Until you do, the
app still detects events and lets you review and blacklist them; only the "add to
calendar" button needs credentials.

## Repo layout

```
src/OutlookScraper.Core/      net8.0, no Windows dependencies — all the logic
src/OutlookScraper.Outlook/   COM interop, and nothing else
src/OutlookScraper.App/       WPF tray shell, toasts, windows
src/OutlookScraper.Cli/       cross-platform harness for tuning prompts
tests/OutlookScraper.Core.Tests/
```

The split is deliberate rather than decorative. Every piece of logic worth testing
lives in `Core`, which targets plain `net8.0` — so the compiler refuses to let COM,
WPF or the registry leak into it, and the whole pipeline is testable on Linux CI.
The Windows projects are a thin shell over it.

## Development

```bash
dotnet test OutlookScraper.Linux.slnf     # 190 tests, runs anywhere
dotnet build OutlookScraper.sln           # needs Windows
```

`OutlookScraper.Linux.slnf` is a solution filter holding Core, the tests and the
CLI. It exists because WPF cannot build on Linux at all, so CI runs the real test
suite on `ubuntu-latest` and compiles the Windows projects separately on
`windows-latest`.

### Tuning the classifier

Prompt quality matters more here than anything else in the app, and iterating on it
through the tray UI would be miserable. The CLI runs the real pipeline against text
fixtures:

```bash
dotnet run --project src/OutlookScraper.Cli -- classify tests/OutlookScraper.Core.Tests/Fixtures/emails
dotnet run --project src/OutlookScraper.Cli -- match frat-rush-pizza fraternity-recruitment-pizza
dotnet run --project src/OutlookScraper.Cli -- schema
```

The fixtures include the cases that are actually hard: `$5 Pizza Night` and "free
coffee with any purchase" are *not* free food, while "lunch will be provided" and
"we'll feed you" are. If you change the prompt, run these first.

## How it decides

Every message that is not a duplicate, an auto-reply, or a non-mail item goes to the
model — there is deliberately **no keyword pre-filter**. "Refreshments provided",
"catered" and "we'll feed you" all evade a naive `free|food|pizza` regex, and recall
is the entire reason for using a language model instead of a regex.

The model answers against a JSON schema, which Ollama compiles into a grammar so the
output is structurally guaranteed. Alongside the event details it emits a
**topic tag** describing the recurring *type* of event —
`fraternity-recruitment-pizza`, not `sigma-chi-rush-oct-14-pizza`. That distinction
is what makes blacklisting generalize to future emails instead of muting exactly one
message.

### Blacklisting

Blacklisting works on that topic tag, and matching runs as a four-stage cascade:

| Stage | Test | Catches |
|---|---|---|
| 0 | Same category? | Stops a frat-pizza rule ever touching a chemistry seminar |
| 1 | Identical normalized key | `free-pizza-club-meeting` = `club-meeting-with-free-pizza` |
| 2 | Token overlap ≥ 0.60 | `cs-club-pizza-night` ≈ `cs-club-pizza` |
| 3 | Embedding cosine ≥ 0.90 | `boba-social` ≈ `bubble-tea-mixer` |

Stages 0–2 are pure string work and always available. Stage 3 only runs if you have
an embedding model pulled; without one the app degrades to text matching, which
still handles the common case of one listserv sending near-identical mail forever.

Between 0.82 and 0.90 a match is **soft-suppressed**: hidden from toasts, but listed
in the Suppressed tab with a one-click "not the same thing" that permanently stops
that rule matching that tag. Silently swallowing an event you wanted is the worst
thing this app could do, so nothing ever disappears without a trace — every
suppression records which rule fired, at which stage, with what score.

Blacklisting is also retroactive: it immediately sweeps everything already waiting,
so muting one frat pizza email clears the other four in the queue.

Note that suppression necessarily happens *after* the model runs, since the tag is
the model's own output. That costs nothing — the message was going to be classified
either way.

## Which Outlook?

COM automation works with **classic** Outlook only. Microsoft removed it from the
new Outlook client in favour of Graph, so this app cannot see mail there.

To check which you have: classic Outlook has `File → Options`; new Outlook has a
"New Outlook" toggle in the top right. If the tray tooltip says "Outlook: not
running — waiting" while Outlook is plainly open, this is almost certainly why.

The mail reader sits behind an `IMailSource` interface so a Graph backend could be
added later, but that is the whole extent of the hedge — none of it is built.

## Notes

- **Outlook can be closed and reopened freely.** The app attaches to a running
  Outlook and never launches or quits it, waits quietly when it is closed, and
  sweeps for anything it missed on reconnect.
- **Ollama can be down.** Work queues up rather than being lost, and resumes when it
  comes back. The sweep watermark only advances once messages actually finish.
- **New mail is detected three ways** — an event, a second event, and a periodic
  sweep. That redundancy is not paranoia: Outlook's `ItemAdd` event is documented
  not to fire when more than sixteen messages arrive at once, which is exactly what
  a listserv burst looks like.
- **Toasts for unpackaged apps fail silently** if the executable moves. The app
  re-checks its registration at every startup, and Settings has a "send test
  notification" button.
- Logs are in `%LOCALAPPDATA%\OutlookScraper\logs\`. Settings are a hand-editable
  `settings.json` in the same folder.
