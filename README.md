# DialScript

An IVR testing tool for Amazon Connect and beyond.

DialScript places a real phone call to your IVR and walks through a scripted
sequence of touch-tone and spoken inputs. It records the call and transcribes
it, so you can confirm your call flow behaves the way you expect. It works
for a single test call or a batch of them.

**Requires Windows 10 or 11.** There's no macOS or Chromebook version.

## Getting Started

1. **Extract the zip** to any folder you like. There's no installer and no
   admin rights are needed. Keep the `runtimes` folder next to
   `DialScript.exe`; it's needed for transcription.
2. **Request your demo key.** DialScript needs a demo key to place calls.
   Message me on LinkedIn at https://www.linkedin.com/in/michaeldludden/ or
   email me at `michaeldludden [at] gmail [dot] com`, and I'll send you one.
   It looks like `DS-XXXX-XXXX-XXXX`.
3. **Double-click `DialScript.exe`** to launch, and enter your key when asked
   (you only do this once).
   - Windows may show a "Windows protected your PC" warning the first time,
     since this is a small independent tool rather than a widely distributed
     signed app. Click **More info**, then **Run anyway**.

The app comes pre-loaded with two sample scripts and a sample batch, so you
can try it right away.

## What's Inside

- **Environment:** switch between Production, Development, and Integration,
  or add your own. Each keeps its own Phone Book, scripts, and batches, so
  testing in one never mixes with another.
- **Phone Book:** save numbers you test against regularly, with autocomplete
  when dialing.
- **Script Selector:** save, search, and reload test scripts by name, or
  import one someone shared with you.
- **Call Target & Step Builder:** build the sequence of steps to run once a
  call connects: touch-tone input, spoken text, or hang up.
- **Batch:** group scripts together and run them back-to-back.
- **Batch Scheduler:** run saved batches automatically at a set time while
  the app is open.
- **Recording & transcription:** every call is recorded to your PC and, by
  default, transcribed to text with your script's steps marked where they
  happened.

For a full walkthrough of every feature, see **TESTING.md**.

## Good to Know

- **Test call limit:** this evaluation build allows 15 test calls per demo
  key (single Dials and batch calls combined), and batches are capped at 3
  scripts. The count is kept online with your key, so it can't be reset by
  reinstalling. Once you reach the limit, you'll see a message with my
  contact info if you'd like to keep testing. Calls need an internet
  connection.
- **Phone numbers** are assumed to be US/Canada (+1).
- **Scripts run on a fixed timeline.** Each call follows its steps and
  pauses as written; it doesn't listen and react to what the IVR says
  during the call. Adjust each step's pause to match your prompts.
- **Recordings stay on your PC** (or in your chosen Recording Location).
  Once a recording is saved and verified locally, it's deleted from the
  calling service.
- **Transcription runs offline** on your PC and adds roughly 10-30 seconds
  per call. The first time, it downloads a ~140 MB speech model. Untick
  **Transcribe Recording** to skip it, or use **Transcribe a Saved
  Recording...** later. Automatic transcripts can contain mistakes; the
  recording is always the source of truth.
- **Logging is on by default**, writing to
  `%AppData%\DialScript\Production\Logs\DialScript-log.txt` for both single
  scripts and batches. Use **Change...** to pick a different file or
  **Stop** to turn it off.
- **Your data** (Phone Books, scripts, batches, logs, recordings, and
  settings) is kept in `%AppData%\DialScript\`.
