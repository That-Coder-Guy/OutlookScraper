# Setup

Two things need one-time setup: Ollama (required) and Google Calendar (optional —
only the "add to calendar" button depends on it).

## Ollama

Install from [ollama.com](https://ollama.com), then pull a model:

```
ollama pull llama3.1:8b
```

Any instruction-following model that supports structured output will work.
`llama3.1:8b` is a reasonable default on a machine with 8 GB of VRAM; `qwen2.5:14b`
is noticeably better at the ambiguous cases if you have room for it. Change it under
Settings → Ollama, where the dropdown lists whatever you have installed.

Optionally, for smarter blacklist matching:

```
ollama pull nomic-embed-text
```

This is genuinely optional. Without it, blacklist rules still match on wording and
word order — they just will not recognise that `boba-social` and `bubble-tea-mixer`
are the same kind of event. The app tells you once, in Settings, and otherwise does
not nag.

Verify the app can see Ollama with:

```
dotnet run --project src/OutlookScraper.Cli -- tags
```

## Google Calendar

You are creating your own OAuth client, so the app talks to Google as *you* rather
than through anyone else's project.

1. Go to the [Google Cloud Console](https://console.cloud.google.com) and create a
   project (any name).
2. **APIs & Services → Library →** search for **Google Calendar API** → **Enable**.
3. **APIs & Services → OAuth consent screen:**
   - User type: **External**
   - Fill in an app name, your email as support contact, and your email as developer
     contact. Nothing else is required.
4. **Scopes:** add `https://www.googleapis.com/auth/calendar.events`.

   Only this one. It permits creating and editing events, and nothing else — the app
   deliberately does not ask for permission to read your calendar list.
5. **Publish the app to Production.**

   This step is easy to skip and you will regret skipping it. While the consent
   screen is in *Testing*, Google expires refresh tokens after **seven days**, so
   the app appears to log itself out every week forever. Publishing removes that
   limit. Since the app is unverified, you will see a "Google hasn't verified this
   app" screen once on first sign-in — click **Advanced → Go to (your app name)**.
   For a personal tool this is the right trade.
6. **APIs & Services → Credentials → Create credentials → OAuth client ID:**
   - Application type: **Desktop app**
   - Create, then **Download JSON**.
7. Rename the downloaded file to `client_secret.json` and put it in:

   ```
   %LOCALAPPDATA%\OutlookScraper\client_secret.json
   ```

The next time you press "Add to Calendar", your browser opens for consent. The token
is stored encrypted with DPAPI under `%LOCALAPPDATA%\OutlookScraper\google-token\`,
tied to your Windows account — copying it to another machine will not work, by design.

### Choosing a calendar

Settings → Calendar → *Target calendar* defaults to `primary`, which is your main
one. To use a different calendar, paste its ID (in Google Calendar: calendar
settings → *Integrate calendar* → *Calendar ID*).

It is a text box rather than a dropdown because listing your calendars would require
a broader permission grant than this app is willing to ask for.

## Outlook

Nothing to configure — the app attaches to your running Outlook automatically. But
it must be **classic** Outlook; the new client has no COM automation and the app
cannot see mail there. See the README for how to tell them apart.

By default it watches your Inbox. To watch subfolders too, add them under
Settings → Mail using backslash paths relative to the inbox, e.g. `Campus\Events`.

## Where things live

```
%LOCALAPPDATA%\OutlookScraper\
  data.db              suggestions, blacklist rules, processing state
  settings.json        hand-editable; delete it to reset to defaults
  client_secret.json   your Google OAuth client (never committed)
  google-token\        DPAPI-encrypted refresh token
  logs\                rolling daily logs, kept 14 days
```

## Troubleshooting

**Tray says "Outlook: not running — waiting" but Outlook is open.**
You are almost certainly on new Outlook. See the README.

**No notifications appear.**
Toast registration for unpackaged apps breaks silently if the executable moves.
Settings → General → **Send test notification** will tell you. The app also repairs
its registration at each startup, so restarting it often fixes this.

**"Ollama: model not installed".**
Settings names the exact `ollama pull` command. The app will not silently substitute
a different model than the one you chose.

**An event you wanted got hidden.**
Review window → Suppressed tab. Every hidden item shows which rule caught it and how
confidently. "Restore" both brings it back and stops that rule matching it again.

**Everything is being classified as free food.**
Raise Settings → Ollama → *Minimum confidence* to `high`, or try a larger model.
Use the CLI fixtures to see how a model handles the known-hard cases before
committing to it.
