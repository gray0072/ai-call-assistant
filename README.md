# AI Call Assistant

A Windows tray app that records calls in any app (Teams, Zoom, Meet, Telegram…), transcribes them locally with speaker separation (English, Russian, Swedish), and optionally has an AI assistant read the transcript during the call and write a running summary, suggestions on what to say or ask, and doubts about what was said.

Windows, .NET 10, WinForms + NAudio, Whisper.net, sherpa-onnx; the assistant uses LM Studio (local) or OpenRouter (cloud).

## The window

Opens when the app starts, on a double-click on the tray icon, from the tray menu, or by launching the app (or its desktop shortcut) again. Closing it does not stop anything — the app keeps running in the tray.

- **Top bar:** project, its notes, recording state and Start / Pause / Stop.
- **Participants:** one row per voice heard in the call. Type a name next to a voice — the transcript file is rewritten with it, the assistant sees it, and the voice is recognized in the next calls (see [Known voices](#known-voices)). A name recognized by voice is shown in italics; type it to confirm or type another to correct it. Your own row ("Me") remembers your name for all calls; names given in a project are offered for the next calls of that project.
- **Transcript:** the live draft during the call, replaced by the accurate transcript after it.
- **AI assistant:** on/off, model, how often it updates, "Update now", the notes, and a box to ask it a question.

The windows are laid out with layout panels and scale per monitor (PerMonitorV2), so they stay sharp on 4K screens and when moved between monitors with different scaling. The tray icon is drawn as vectors at the size the taskbar asks for.

## Projects and folders

The project field at the top takes an existing project from the list or a new name (Enter). Each project is a subfolder of the recordings folder:

```
Documents\AI Call Assistant\
    2026-11-20_10-00-00.mp3 / .txt / .assistant.md     <- calls without a project
    Mobile app\
        2026-11-23_12-01-02.mp3
        2026-11-23_12-01-02.txt
        2026-11-23_12-01-02.assistant.md
```

- A call is recorded into the folder of the project chosen at its start.
- Changing the project of the current call (during it or after) moves its recording, transcript and notes to the new project's folder once the call is finished.
- A folder created in the recordings folder (e.g. in Explorer) shows up as a project.
- "Folder" next to the project opens its folder; "Notes…" holds background for the assistant — goals, terms, who is who — sent with every request.

Transcribed files ("Transcribe file…") are not moved: the transcript is written next to the file.

## Known voices

When you name a voice, the app remembers a voice print of it (an averaged speaker embedding) and names that person automatically in later calls — in any project, and in transcribed files too (your own voice is learned from the microphone track once you have named "Me").

- During a call a voice is compared after each phrase of 2 s or more (average of its phrases so far); after the call the accurate pass compares the print of the whole call again and corrects the names. Each known person is given to at most one voice per call.
- A name is given only when the voice is clearly closer to one person than to anyone else (similarity ≥ 0.80 for the whole call, ≥ 0.82 during it, and at least 0.06 above the second best). On a test call the same person scored 0.88–0.90 against their print from another part of the call, an unknown person at most 0.74 — a missed name is preferred to a wrong one.
- Recognized names are shown in italics. Typing a name (the same or another) confirms or corrects it, and recognition never touches that voice again in the call; clearing the name of a finished call also forgets that call's sample of the voice.
- One sample per person per call, the latest 12 are kept. "Known voices…" under the participants lists the people with the number of calls and the last time heard, and can forget them.
- Voice prints are biometric data: they are kept only in `voices.json` next to the exe (not in git, never sent anywhere).

## Recording

- Records the **output** (WASAPI loopback of the default playback device — the other participants) and the default **microphone** at the same time and mixes them into one file.
- MP3 96 kbps stereo (LAME picks 32 kHz) — about 43 MB per hour.
- Files: `yyyy-MM-dd_HH-mm-ss.mp3`, the transcript next to it as `.txt`, the assistant's notes as `.assistant.md`. Default folder `Documents\AI Call Assistant` (+ project subfolder), changed with "Recordings folder…".

## Speech recognition

Fully local: English, Russian and Swedish, the language is detected per phrase (Whisper chooses only among these three), so languages can alternate within a call.

While recording with recognition on, the microphone and the speaker output are also written to two temporary tracks (16 kHz WAV, `%TEMP%\AiCallAssistant`, ~115 MB per hour each). After Stop they give the accurate transcript: microphone speech is "Me", the speaker output is split into voices (pyannote) as "Speaker 1", "Speaker 2", … Each accurate voice takes over the live voice it overlaps most in time, so names given during the call stay with the right person. Then the tracks are deleted.

| Mode | How it works |
|---|---|
| Live (default) | During the call each phrase appears a couple of seconds after it was said; voices from the speakers are told apart by voice prints on the fly (rougher). After Stop the draft is replaced by the accurate transcript. Needed for the assistant during the call. |
| After recording | Only the accurate transcript after Stop. |
| Off | Recording only. |

"Transcribe file…" handles any audio file; it has no separate tracks, so voices are split over the whole file and "Me" is not distinguished. The assistant can answer questions about it too. Transcriptions run one at a time. A transcript file is replaced only as a whole.

## AI assistant

Turned on with the checkbox in the window or in the tray menu. It reads the transcript and writes short notes:

- **Summary** — the call so far, decisions, agreed next steps (carried over from update to update, so long calls fit into a small model);
- **Suggestions** — an answer to a question put to you, a question worth asking, a topic not covered yet;
- **Worth checking** — something that sounds factually wrong, contradictory or unclear (only when there is such a thing).

You can also ask it anything about the call in the box below the notes; it answers in the language of the question. After the call, once the accurate transcript is ready, it writes the summary of the call: decisions, action items, open questions. Notes are written in the interface language and saved next to the recording (`.assistant.md`).

**When it updates during a call:** only when there is new speech, and either the chosen interval has passed (30 s, 1, 2 or 5 min; default 1 min) or someone else has just asked a question (a phrase ending with "?" — then after 15 s at most). "Only on request" turns automatic updates off; "Update now" and questions always work. Requests never overlap: they queue.

**Where the model runs** (Settings…):

- **Locally in LM Studio** — the transcript never leaves the computer. The list shows the chat models downloaded in LM Studio, loaded ones first; `google/gemma-4-e4b` is the default (~12–20 s per update on an RTX 4070 Laptop next to Whisper, fits into 8 GB of video memory together with it). If LM Studio's server is not running, the app starts it with the `lms` tool. The amount of transcript sent is limited by the context length LM Studio loaded the model with.
- **In the cloud via OpenRouter** — one API key gives access to hundreds of models. After the key is checked (it shows what has been spent), the list shows the models with prices per million input / output tokens, most popular first. The cost of the current call is shown next to "Update now". The key is stored in `settings.json` encrypted for the current Windows user (DPAPI).

The last chosen model of each source is remembered.

## Status and errors

- Tray icon: the app icon when idle, red dot while recording, yellow while paused.
- While something is processed (downloading models, loading a model, transcribing) a blue progress ring is drawn around the icon; it spins while the percentage is unknown. The stage and percentage are shown in the window, as the first menu item and in the icon tooltip.
- An error shows a Windows notification, a red "!" badge on the icon and a menu item with the error text. Clicking it (or the notification) opens `AiCallAssistant.log` and removes the badge. Caught: unavailable device at start, device disconnected during recording, failure to write to disk (recording stops and is saved), model download and recognition errors, assistant (LLM) errors, any unhandled exception.

## Interface language

English, Russian, Swedish (all strings are in `Strings.cs`). The default is the Windows display language (`CultureInfo.CurrentUICulture`; English if it is none of the three), changed in the "Language" menu and applied at once. Speaker labels in the transcript ("Me" / "Я" / "Jag", "Speaker N" / "Спикер N" / "Talare N") use the language chosen when the call started.

## Speech models

Downloaded automatically to the `models` folder next to the exe (not in git) as soon as a recognition mode is chosen:

| File | Size | Purpose |
|---|---|---|
| `ggml-large-v3-turbo-q8_0.bin` | 834 MB | Whisper — speech to text |
| `wespeaker_en_voxceleb_resnet34_LM.onnx` | 25 MB | voice print |
| `pyannote-segmentation-3-0.onnx` | 6 MB | who speaks when |
| `silero_vad.onnx` | 0.6 MB | finding phrases |

Whisper is loaded into video memory (~850 MB) only while recognizing — when a live recording or a transcription starts — and unloaded after a minute of idleness. Loading takes 2–4 seconds; in live mode audio is buffered meanwhile and processed right after. The other models are small and created for each operation.

Whisper runs on the discrete GPU via Vulkan (only the regular driver is needed). The integrated GPU is not used: on a laptop with Radeon 890M + RTX 4070, whisper.cpp picked the integrated one by default and crashed loading the model. Without a discrete GPU recognition runs on the CPU, noticeably slower.

Speed on an RTX 4070 Laptop: a 3.5-minute call is transcribed in about 1–1.5 minutes (voice separation on the CPU ~20 s, Whisper ~1 s per phrase).

## Build and run

```
dotnet build -c Release
bin\Release\net10.0-windows\AiCallAssistant.exe
```

Native libraries (`libmp3lame.*.dll`, `runtimes\...`) must sit next to the exe, so single-file publish does not work. `settings.json`, the log and `models` are created next to the exe.

## Limitations

- The devices are the Windows defaults at the moment recording starts. If the call app sends sound to another device (e.g. a headset set as the "communications device"), the other participants will not be in the recording.
- A device disconnected during recording is recorded as silence from then on (you get a notification); there is no switching to a new device.
- Without headphones the microphone hears the speakers, and the others' speech may appear in the transcript again labelled "Me".
- The live draft may split one person into two speakers or merge similar voices; the accurate transcript after Stop fixes that.
- The assistant sees only what speech recognition heard; it can be wrong, and a small local model more so. It does not search the web.
- Voice recognition depends on the voice separation: if two people were merged into one voice when you named it, the print is a mix of both. Short calls give weaker prints; it gets better as more calls of a person are remembered.
