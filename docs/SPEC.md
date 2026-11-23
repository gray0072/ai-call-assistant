# AI Call Assistant — specification

What the app must do. How it is used is in the [README](../README.md); rules for working on the code are in [AGENTS.md](../AGENTS.md).

## 1. Purpose

A Windows app that lives in the tray, records calls in any app (Teams, Zoom, Meet, Telegram…), turns them into text locally with speakers told apart, and — if the user wants — has an AI assistant follow the call and help: a running summary, suggestions on what to say or ask, doubts about what was said, answers to the user's questions, and the summary of the call afterwards.

Main scenario: a group work call in English, Russian or Swedish, sometimes switching languages mid-call. The user picks the project, starts recording, types names next to the voices as people speak, glances at the assistant's notes, and after Stop gets an accurate transcript with names and a summary next to the MP3:

```
[00:01:12] Me: Hi, let's go through the task…
[00:01:15] Anna: Yes, I checked the deploy…
```

## 2. Platform

| Item | Value |
|---|---|
| OS | Windows 10/11 x64 |
| Runtime | .NET 10, WinForms |
| Audio | NAudio (WASAPI), NAudio.Lame (MP3) |
| Speech to text | Whisper.net 1.9 (whisper.cpp), Vulkan backend, CPU fallback |
| Voices and phrases | sherpa-onnx (pyannote segmentation, WeSpeaker, Silero VAD) |
| Assistant | Any model in LM Studio (local) or on OpenRouter (cloud), OpenAI-compatible chat completions with streaming |
| Network | Downloading speech models once; OpenRouter only if chosen |
| Instances | One per user (named mutex); starting it again shows the running one's window |
| Displays | Sharp and proportionate on 4K and mixed-DPI setups (PerMonitorV2) |

## 3. Recording

- **R1.** The output of the default playback device (WASAPI loopback — the other participants) and the default microphone are recorded at the same time.
- **R2.** Devices are fixed when recording starts; there is no device choice (see §11).
- **R3.** Both sources are mixed into one file; the channels of the MP3 are not separated.
- **R4.** MP3 96 kbps stereo (sample rate chosen by LAME, usually 32 kHz) — about 43 MB per hour.
- **R5.** File name is the start time: `yyyy-MM-dd_HH-mm-ss.mp3`.
- **R6.** Default folder `Documents\AI Call Assistant`, changed from the tray menu after checking the folder is writable; a project's recordings go to a subfolder named after it (§6). A new folder applies from the next recording.
- **R7.** Start / Pause–Resume / Stop are available in the window and in the tray menu.
- **R8.** On exit during a recording the file is closed properly first; if a transcript is not finished, the app asks for confirmation.
- **R9.** A device disconnected during recording is recorded as silence, with a notification. A failure to write to disk stops the recording and keeps what was recorded. An unavailable device at start means no recording and an error.

## 4. Speech recognition

### 4.1 Modes (window top bar and tray menu, default Live; locked while recording)

| Mode | Behaviour |
|---|---|
| Live | A draft is built during the call, phrase by phrase, a couple of seconds after each was said; voices from the speakers are told apart by voice prints on the fly. After Stop the draft is replaced by the accurate transcript. |
| After recording | Only the accurate transcript after Stop. |
| Off | Recording only; speech models are neither downloaded nor loaded. |

### 4.2 Accurate transcript of a recording

- **T1.** With recognition on, the microphone and the output are also written to two temporary tracks (16 kHz mono WAV, `%TEMP%\AiCallAssistant`, ~115 MB per hour each).
- **T2.** After Stop: phrases are found on the microphone track (Silero VAD) — that is "Me"; the output track is split into voices (pyannote + WeSpeaker) as "Speaker 1", "Speaker 2", …; each piece is recognized by Whisper; phrases are sorted by time.
- **T3.** Each voice of the accurate transcript takes over the live-draft voice it overlaps most in time (one-to-one); new voices get the smallest free "Speaker N". Names given during the call therefore stay with the right person.
- **T4.** The temporary tracks are deleted after the transcript.

### 4.3 Transcribing a file

- **T5.** "Transcribe file…" takes any audio file NAudio reads; voices are split over the whole file and "Me" is not distinguished. The result is shown in the window (unless a call is being recorded) and the assistant can be asked about it.
- **T6.** Transcriptions run strictly one at a time; the queue is shown.

### 4.4 Languages

- **T7.** English, Russian and Swedish; the language is detected per phrase, Whisper chooses only among these three.
- **T8.** Known Whisper hallucinations on silence ("Thanks for watching", "Субтитры…", "Textning", "Tack för att du tittade" etc.) are filtered out.

### 4.5 Transcript file

- **T9.** `.txt` next to the recording with the same name, UTF-8, one line per phrase: `[hh:mm:ss] Name: text`.
- **T10.** The file is rendered from the session: renaming a voice rewrites it. It is replaced only as a whole (written to `.txt.tmp`, then moved).
- **T11.** Default labels use the interface language chosen when the call started: "Me" / "Я" / "Jag", "Speaker N" / "Спикер N" / "Talare N".

## 5. Window

- **W1.** Opens on app start, on a double-click on the tray icon, from the tray menu, and when the app is started again. Closing it never stops recording, transcription or the assistant.
- **W2.** Top bar: project (choose an existing one or type a new name), project notes, project folder, recognition mode (§4.1; can't be changed while recording), recording state with duration and processing progress, Start / Pause / Stop.
- **W3.** Participants: one row per voice of the current call — default label (in the voice's color), editable name, number of phrases. Rows appear as voices are heard.
- **W4.** Transcript: the live draft or the accurate transcript, each speaker in its own color; stays scrolled to the end unless the user has scrolled up. If live recognition is off, a hint says when the transcript will appear (after the call, or never with recognition off).
- **W5.** Assistant panel: on/off, current source and model, Settings…, update interval, Update now, cost so far (cloud), the Summary pane, the Notes pane, a question box. Without live recognition everything but Settings… is disabled and a line explains that the assistant needs live recognition.
- **W6.** The current call is the one being recorded, otherwise the last one recorded or transcribed.

## 6. Projects and participants

- **P1.** A project has a name, optional notes (background for the assistant) and the names given to people in its calls. Projects are listed most recently used first.
- **P2.** Each project is a subfolder of the recordings folder (its name with characters invalid in file names replaced by `_`); calls without a project stay in the recordings folder itself. Subfolders found there (e.g. created in Explorer) are added as projects.
- **P3.** A call is recorded into the folder of the project chosen when it starts. Changing the project of the current recorded call — during it or after — changes what the assistant is told and moves the recording, transcript and notes to the new project's folder as soon as nothing writes to them any more (after the accurate transcript, or at once if the call is finished). On a name clash the moved files get a " (2)" suffix. Transcribed files are never moved.
- **P4.** A name typed for "Me" becomes the user's name for all calls. Other names are remembered in the project and offered (autocomplete) when naming voices in its next calls.
- **P5.** Clearing a name returns the voice to its default label.

### Known voices

- **V1.** Naming a voice remembers its voice print (WeSpeaker embedding, normalized) under that name in `voices.json` next to the exe: one sample per person per call (keyed by the call's original file name, so renaming a voice in the same call replaces the sample), the latest 12 per person. Prints come from the accurate pass (length-weighted average of the voice's longest phrases nobody talks over, up to 30; all its phrases if there are none) — when the call's transcript is done, or at once when the name is typed after that. If the accurate pass failed, the average of the live phrase prints is used. "Me" is learned from the microphone track under the user's name.
- **V2.** During a live call, each phrase of 2 s or more from the speakers adds to the voice's running average print, which is compared with the library; a person is named if similarity ≥ 0.82 and ≥ 0.06 above the second-best person. With 4 or more phrases and no match, an earlier recognized name is taken back.
- **V3.** After the accurate pass, voices the user has not named are compared again using whole-call prints (≥ 0.80, same margin) and named, renamed or un-named accordingly; pairs are assigned best first, each person to at most one voice; names the user typed in the call are excluded.
- **V4.** A voice the user has named or cleared in a call is never named by recognition again in that call. Clearing the name of a finished call removes that call's sample.
- **V5.** Recognized names are shown in italics with the similarity in a tooltip; the transcript and the assistant use them like typed names.
- **V6.** "Known voices…" lists people (name, number of calls, last heard, projects) and forgets selected ones after confirmation.
- **V7.** Thresholds come from a 3.5-minute three-person call: same person across its halves 0.77–0.94, different people 0.57–0.76; with the halves as two calls, the two named people were recognized at 0.90 and 0.88 and the unknown one scored at most 0.74.

## 7. AI assistant

### 7.1 What it writes

- **A0.** The assistant works only with live recognition: with any other mode it does not run, the final summary is not written, and the controls are disabled.
- **A1.** Each live update writes, with fixed headings in the interface language: Summary (≤ 5 bullets), Suggestions (≤ 3: an answer to a question put to the user, a question worth asking, a topic not covered), Worth checking (only if something said looks factually wrong, contradictory or unclear). Headings with nothing under them, or only "(None)" and the like, are dropped.
- **A2.** Summary pane: one current summary. Each update is given the previous summary and replaces it with the new one; after the accurate transcript of the call it is replaced by the summary of the whole call — Summary, Decisions, Action items ("who — what — when"), Open questions — marked "whole call".
- **A3.** Notes pane: Suggestions and Worth checking of the latest update only (cleared when the whole-call summary arrives), then the user's questions with answers, newest first, in the language of the question; answers stream in.
- **A4.** While an update is being written the panes keep the previous text and their titles say "updating…"; titles show the time of the last update; a failed update shows its error under the summary. Every result is also appended to `<recording>.assistant.md`.
- **A4a.** An empty answer is retried once; the log records the finish reason and how much hidden reasoning the model produced.

### 7.2 When it runs

- **A5.** Automatically only while recording, only if there is new speech since the last update, and only when either the chosen interval has passed (30 s / 1 / 2 / 5 min; default 1 min) or someone other than the user has said a phrase ending with "?" and at least 15 s have passed since the last update.
- **A6.** "Only on request" disables automatic updates; "Update now" and questions always run.
- **A7.** Requests run one at a time in order; nothing is sent while one is in progress.
- **A8.** A failed request shows the error in the notes and as an app error (§9); the next update retries.

### 7.3 What it is given

- **A9.** System prompt (English): its role, that the transcript comes from speech recognition and may contain errors, which label is the user, the output language, brevity; the project name and notes if any.
- **A10.** The transcript with names, as much of the latest part as fits: for LM Studio the context length the model is loaded with (≈ 2 characters per token, minus room for instructions and the answer), for the cloud up to ~60 000 characters. Its own previous summary and suggestions are given back, so a long call still fits into a small context and suggestions do not repeat.

### 7.4 Models

- **A11.** Source: LM Studio (local, default) or OpenRouter (cloud). The last chosen model of each source is remembered.
- **A12.** LM Studio: the address is configurable (default `http://localhost:1234`); the list shows downloaded chat models, loaded first, with their context; default `google/gemma-4-e4b`. If nothing listens at a local address, the app runs `lms server start` once and retries.
- **A13.** OpenRouter: an API key is entered and checked (spent / limit shown); only then are models listed — chat models with text output, most popular first, with prices per million input / output tokens; batch-only variants are excluded. Requests ask for low reasoning effort. The cost of each answer is summed per call and shown.
- **A14.** The API key is stored in `settings.json` encrypted with DPAPI for the current Windows user.

## 8. Speech models

- **M1.** Stored in `models` next to the exe, not in git; downloaded automatically when a recognition mode is chosen or a file is transcribed, with progress shown.

| File | Size | Purpose |
|---|---|---|
| `ggml-large-v3-turbo-q8_0.bin` | 834 MB | Whisper — speech to text |
| `wespeaker_en_voxceleb_resnet34_LM.onnx` | 25 MB | voice print |
| `pyannote-segmentation-3-0.onnx` | 6 MB | who speaks when |
| `silero_vad.onnx` | 0.6 MB | finding phrases |

- **M2.** Whisper is in video memory (~850 MB) only while recognizing and is unloaded after a minute idle; loading takes 2–4 s, live audio is buffered meanwhile.
- **M3.** Whisper runs on the discrete GPU via Vulkan; the integrated GPU is never used; CPU without a discrete GPU.
- **M4.** Reference: RTX 4070 Laptop transcribes a 3.5-minute call in 1–1.5 min. Whisper (~0.85 GB) and gemma-4-e4b in LM Studio (~4 GB) fit into its 8 GB together.

## 9. Status and errors

- **U1.** Tray icon: the app icon (speech bubble with a sparkle) when idle, red dot while recording, yellow while paused; a progress ring around a dot while processing (spinning when the percentage is unknown). Drawn as vectors at the taskbar's size for the current DPI.
- **U2.** Any error: Windows notification, red "!" badge on the icon, the error text as the first menu item; clicking it opens the log and clears the badge. The same error repeating does not flood notifications (once a minute).
- **U3.** Caught: unavailable or disconnected device, disk write failure, model download and recognition errors, assistant errors, unhandled exceptions on the UI thread and in background tasks; a fatal error shows a message box with the log path.
- **U4.** Log `AiCallAssistant.log` next to the exe; failing to write the log never breaks the app.

## 10. Interface language and display

- **L1.** English, Russian, Swedish, in that order. All strings live in `Strings.cs`; a missing string in any language is a compile error.
- **L2.** Default is the Windows display language; English if it is none of the three. Changed in the tray menu, applied at once, including the open window.
- **L3.** All windows use layout panels and auto-size in 96-DPI units with `AutoScaleMode.Dpi`, the process is PerMonitorV2: no blurry bitmaps, no clipped text at 150–250 % scaling, correct rescaling when a window moves to a monitor with different scaling.

## 11. Out of scope and known limitations

- No choice of recording devices; no switching to a new device after a disconnect.
- Without headphones the microphone hears the speakers, so others' speech may appear again as "Me".
- The live draft may split or merge voices; the accurate transcript fixes it.
- Whisper sometimes assigns a mixed-language phrase to one language and translates part of it.
- Voice recognition inherits the errors of voice separation: a voice merged from two people gives a mixed print. Voice prints are biometric data and stay on the machine.
- The assistant has no web access (see §12).
- No single-file publish (native libraries next to the exe), no installer, no autostart.

## 12. Possible next steps

- Web search for the assistant (postponed): a `web_search` tool (OpenAI-style tool calling, supported by LM Studio and OpenRouter) implemented by the app with a search API, or OpenRouter's web plugin for cloud models, or an MCP search server through LM Studio's `/api/v1/chat` `integrations`.
- Device selection and following the default device.
- Echo cancellation of the speakers in the microphone signal, for calls without headphones.
- Export of the transcript to Markdown / SRT; renaming voices of old transcripts.
- Installer, autostart with Windows, releases on GitHub.
