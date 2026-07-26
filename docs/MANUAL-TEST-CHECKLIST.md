# Manual test checklist

The automated suite covers everything in `Core` — the blacklist cascade, time
resolution, body cleaning, request shapes, storage. What it cannot cover is COM
interop, the tray, toast activation and OAuth, because those need a real Windows
session with a real Outlook and a real Google account.

These are the checks worth doing by hand after any change to the Windows projects.

## Outlook connection

- [ ] **Start the app with Outlook closed.** Tray tooltip reads "Outlook: not
      running — waiting". The app must not launch Outlook itself.
- [ ] **Open Outlook.** Within ~15 s the tooltip changes to "Outlook: connected".
- [ ] **Close Outlook while the app runs.** Tooltip returns to waiting. Critically:
      check Task Manager — there must be **no lingering OUTLOOK.EXE**. A zombie
      process here means a COM reference was not released.
- [ ] **Reopen Outlook.** It reconnects, and mail that arrived while it was closed is
      picked up by the catch-up sweep.
- [ ] **Leave it running for an hour** with Outlook open. Events should still fire —
      if they stop, an event sink was garbage collected, which is the classic failure
      and is silent.

## Mail detection

- [ ] Send yourself one email. It is picked up within seconds.
- [ ] **Send twenty emails at once** (or move twenty into the inbox in one action).
      All twenty are eventually processed. This is the important one: `ItemAdd` does
      not fire above sixteen simultaneous items, so this specifically tests that the
      periodic sweep covers the gap. Wait for one poll interval.
- [ ] Set up an Outlook rule moving mail to a subfolder, add that folder to
      Settings → Mail, and confirm mail landing there is still seen.
- [ ] Send an out-of-office auto-reply to yourself; it is skipped, not classified.
- [ ] Forward the same email twice. The second is recognised as a duplicate body and
      does not pay for a second model call (check the log).

## Classification

- [ ] A real free-food announcement produces a toast.
- [ ] A `$5 pizza night` email does **not**.
- [ ] An email with no date produces a suggestion flagged "no date stated", and its
      Add button is unavailable until a date is supplied.
- [ ] Stop Ollama mid-run. The tray shows it as unreachable, exactly one toast
      appears, and no further nagging. Restart it — queued mail resumes and nothing
      was lost.
- [ ] Set a model name that is not installed. Settings names the exact
      `ollama pull` command rather than silently using another model.

## Toasts

- [ ] Toast body click opens the review window.
- [ ] **Add to Calendar** from the toast, with the app running. Event appears in
      Google Calendar; the toast disappears.
- [ ] **Blacklist** from the toast. If similar events were queued, the confirmation
      names how many were also hidden.
- [ ] **Cold-start activation**: exit the app entirely, trigger a toast beforehand so
      one is sitting in the notification centre, then press its button. Windows
      launches the app, the action applies, and no window is stolen into focus.
- [ ] **Move the published folder** to another path and run it. Settings → General →
      *Send test notification* either works or reports the registration problem —
      it must not fail silently.
- [ ] Trigger four detections within ten minutes. The fourth is folded into a
      summary toast rather than shown individually.

## Calendar

- [ ] First "Add to Calendar" opens the browser for consent.
- [ ] Add the same suggestion twice (toast and review window). Only one event exists.
- [ ] **Delete the event in Google, then add it again.** It comes back. Without the
      cancelled-event recovery path this silently does nothing.
- [ ] Check the event's time is correct, including for an event across a
      daylight-saving boundary.
- [ ] Revoke access at [myaccount.google.com/permissions](https://myaccount.google.com/permissions),
      then add an event. It re-prompts cleanly rather than erroring forever.

## Blacklist

- [ ] Blacklist something, then receive a near-identical email. It does not toast,
      and appears in the Suppressed tab.
- [ ] Suppressed tab shows which rule caught it and at what score.
- [ ] **Restore** a suppressed item. It returns to Pending, and a later matching
      email from the same rule is no longer hidden.
- [ ] Delete a rule in Settings → Blacklist. Everything it suppressed comes back.
- [ ] With no embedding model installed, blacklisting still works on wording — and
      Settings shows the hint once, not repeatedly.

## Lifecycle

- [ ] Enable "Start with Windows", reboot, confirm it starts to the tray without
      opening a window.
- [ ] Launch the exe a second time while running — it does not start a second copy.
- [ ] Exit from the tray menu. The process actually exits, and no OUTLOOK.EXE is
      left behind.
