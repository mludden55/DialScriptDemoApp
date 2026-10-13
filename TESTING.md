# DialScript — Testing Guide

An IVR testing tool for Amazon Connect and beyond.

Thanks for trying this out. This tool places a real phone call, then
automatically walks through a sequence of touch-tone/voice inputs while you
listen to the result.

It comes pre-loaded with two sample scripts and a sample batch so you can see
it working immediately, before building anything of your own.

## A. Installing

There's no installer — it's a portable app that runs from any folder.

1. Extract the zip to any folder you like (Desktop, Downloads, wherever).
   Keep the `runtimes` folder next to `DialScript.exe`; it's needed for
   transcription.
2. **Get your demo key.** Message me on LinkedIn at
   https://www.linkedin.com/in/michaeldludden/ or email me at
   `michaeldludden [at] gmail [dot] com`, and I'll send you one. It looks like
   `DS-XXXX-XXXX-XXXX`.

## B. Running it

Double-click `DialScript.exe`. A window titled "DialScript" should open. No
admin rights are needed. The first time you place a call, it asks for your demo
key and remembers it.

- **Windows may show a "Windows protected your PC" warning** the first time
  you run it, since this isn't a widely-distributed signed app. Click **"More
  info"** → **"Run anyway"**. This is normal for small independent tools and
  not a sign anything is wrong.

## C. Choosing an environment

At the top of the app is an **Environment** selector, defaulting to
**Production**, **Development**, and **Integration**. Each one keeps its own
completely separate Phone Book, scripts, and batches — switching environments
is like switching to a different workspace. The sample scripts/batch/phone
number are pre-loaded into **Production** specifically.

Click **+ Add Environment** to create your own (e.g. "QA" or "Staging") if
you want a workspace beyond the three defaults.

## D. Adding a phone number

The left side of the app has a **Phone Book** section:

1. Type a 10-digit number in the "Phone Number" box — it auto-formats as you type.
2. Optionally give it a description (e.g. "Support Line").
3. Click **+ Add Phone Number**.

It now appears in the list below, and will show up as a suggestion when you
start typing that number (or its description) elsewhere in the app.

## E. Creating a script

A **script** is a phone number plus an ordered sequence of steps to run once
the call connects.

1. Under **Script Selector**, click **+ New Script**. This reveals the Call
   Target and Step Builder sections.
2. In **Call Target**, enter or select a phone number to dial.
3. In **Step Builder**, click **+ Add Step** for each step you want:
   - **Numeric** — sends touch-tone input (digits, `#`, `*`)
   - **Voice** — speaks text aloud
   - **Hang Up** — ends the call at that point
   - Each step has its own "Pause(s)" — how long to wait before that step
     runs. Real IVRs vary a lot in how long their prompts take, so this is
     manual to tune, not automatic.
4. Click **Save Script**, give it a name, and choose where to save it.

Try loading one of the two pre-loaded sample scripts from **Script
Selector** to see a real example before building your own. Received a
script from someone else? Use **Import Script...** next to **+ New Script**
to bring it into your own Script Selector instead of building it from
scratch — see the **How to share scripts with others** help link at the bottom of the app for
more on sharing scripts both ways.

## F. Creating a batch

A **batch** runs several scripts back-to-back, one call at a time.

1. On the right side, click **Create Batch**.
2. Select the scripts you want in the batch (**note: batches are currently
   capped at 3 scripts**).
3. Click **Save Batch**, give it a name, and choose where to save it.

A sample batch ("Batch-Test 1") combining the two sample scripts is already
loaded for you to try under **Run Batch**.

## G. Running things

- **Single script/call:** load a script (or build a new one), then click the
  green **Dial** button.
- **Batch:** under **Run Batch**, search/select a saved batch, then click
  **Run Batch**. It dials each script's number in turn, waiting for each call
  to finish before starting the next.
  To move a batch's file somewhere else (e.g. a shared network folder),
  select it and click **Change...** next to **Batch Location**. Hover over
  any location to see its full path.
- Every call is automatically recorded, downloaded, and played back once it
  ends — no extra setup needed. It saves locally under
  the app's own data folder (or a different folder if set via **Recording
  Location**). For a single Dial it opens the file for playback (untick **Play
  recording when finished**, next to **Import Script...**, to turn that
  off for all scripts); for a batch it opens the folder once at the end so
  you can go through them.
- Once the local file is saved and verified, the recording is deleted from
  the calling service automatically — the local copy is the only one kept.
  If the save fails, the online copy is left alone so nothing is lost. If you
  see a **WARNING** that a recording couldn't be deleted, your recording is
  still saved on your PC. Let me know so I can check it.
- **Transcribe Recording** (on by default): each call's
  recording is turned into text in the Status panel and log file, with the
  script's steps (e.g. `Tester Numeric: 1`) placed where they happened. It
  adds roughly 10-30 seconds per call, so untick it to skip that. Turned it off? Click **Transcribe a
  Saved Recording...** and pick the `.mp3` to transcribe it later. (The
  `.steps.json` saved next to each recording is what lets a later
  transcription include the script's steps — keep it with the `.mp3`.)
- The **Status** panel (and **Batch Status** on the right) shows what's
  happening as it runs.

## H. Automating batch runs

Below Batch Status is **Batch Scheduler** — it runs one or more saved
batches for you automatically, instead of clicking Run Batch by hand.

**Important:** this only fires while the app is open. It's not a background
service, so a schedule due while the app is closed simply doesn't run — not even
late, when you next open the app. Instead: a one-time schedule is turned off and
its card shows it wasn't run, and a daily/weekly one skips that occurrence
and waits for the next one (either way, it's noted in the schedule's log). Likewise, creating or
re-enabling a daily/weekly schedule whose time has already passed today
doesn't run it now; it first runs at the next occurrence.

To set one up, click **+ Add Schedule** and fill in:
- **Name** — whatever helps you recognize it later.
- **Select Batch(es)** — one or more saved batches to run, in order.
- **When** — Once (a specific date and time), Daily, or Weekly (a day of
  the week plus a time).
- **Enabled** — on by default; uncheck to save it without activating it yet.
- **Log Location** — required, since this runs unattended — this is where
  you can check afterward to see exactly what happened.

Each saved schedule shows as its own card with its **Next run** date and
time, plus **Enable/Disable** and **Delete** buttons. Like everything else, schedules are specific to
whichever **Environment** is currently selected when you create them.

## Testing limits

This is an evaluation build. To keep it bounded:
- **Up to 15 total test calls per demo key** (individual Dial + each script run
  within a batch, combined). The count is kept online, so reinstalling doesn't
  reset it. You'll see a message once you hit that limit.
- **Batches are capped at 3 scripts.**

If you'd like to test further, reach out — contact info is in the message
you'll see once the limit is reached.
