# AGENTS.md

Guidance for AI coding agents (and people) working on AI Call Assistant. What the app does: [README.md](README.md). What it must do: [docs/SPEC.md](docs/SPEC.md).

## Languages

- **English is the main language of the project**: code, comments, identifiers, docs (README, SPEC, this file), commit messages, log messages and the prompts sent to the LLM.
- **The app also supports Russian and Swedish**, besides English:
  - User interface: every user-visible string lives in `Strings.cs` with an English, a Russian and a Swedish version (in that order). Strings are `required` properties, so adding one to a single language does not compile — add all three. Keep the Swedish natural, not word-for-word.
  - Speech recognition: Whisper picks English, Russian or Swedish per phrase (`SpeechEngine.Languages`). Hallucination filters (`SpeechEngine.Hallucination`) cover all three.
  - The assistant writes in the interface language (`Strings.AiLanguageName`, headings `Strings.AiHead*`) and answers questions in the language of the question.
- Never put user-visible text directly in code outside `Strings.cs`. Exceptions: the product name `AppInfo.Name`, model ids, URLs.

## Display: 4K and mixed DPI

The app must look sharp and proportionate on 4K monitors (150–250 % scaling) and when a window moves between monitors with different scaling.

- The process is PerMonitorV2 (`ApplicationHighDpiMode` in the .csproj).
- Every form calls `Ui.Prepare(this)` first and `Ui.Done(this)` last: 96-DPI auto-scale dimensions with `AutoScaleMode.Dpi`, Segoe UI 9 pt.
- Lay out with `TableLayoutPanel` / `FlowLayoutPanel`, `AutoSize`, `Dock` and `Anchor`. Sizes in code are in 96-DPI units; pixel values computed at run time (splitter positions etc.) go through `LogicalToDeviceUnits` or proportions of the actual size, after the form is shown.
- No raster images in the UI. Icons are drawn as vectors (`AppIcon.Paint`, `TrayApp.DrawIcon`) at the size Windows asks for; `app.ico` is generated from `AppIcon.CreateIco()` with frames from 16 to 256 px.
- Check new UI at the real scale: screenshot the window on the 4K screen, not only at 100 %.

## Build and run

```
dotnet build -c Release
bin\Release\net10.0-windows\AiCallAssistant.exe
```

- Before building into `bin`, the running app must be closed — but **first make sure it is not recording or transcribing**: no `*.wav` in `%TEMP%\AiCallAssistant` and no `.mp3` growing in the recordings folder. Killing it mid-call loses the end of the recording.
- `models/` (≈0.9 GB), `settings.json` and the log live next to the exe in `bin\Release\net10.0-windows`; never delete that folder wholesale.
- Native libraries must sit next to the exe; single-file publish does not work.
- There is no test project. To check logic without the tray UI, build a scratch console project outside the repo that compiles the needed source files (`<Compile Include="…/CallAssistant.cs" />` etc.) and defines its own `AppInfo`.

## Architecture

| File | Role |
|---|---|
| `Program.cs` | Single instance, "show window" signal from a second instance, global error handlers, `AppInfo` |
| `TrayApp.cs` | Tray icon and menu; owns recording, transcription queue, the current `CallSession` and its `CallAssistant` |
| `AssistantForm.cs` | Main window; only displays state owned by `TrayApp`, closing it stops nothing |
| `AiSettingsForm.cs` | LM Studio / OpenRouter choice, API key, model list |
| `CallSession.cs` | Phrases with speaker keys (`@me`, `@other`, `#N`), typed and recognized names, voice prints, writing the transcript, mapping draft voices to the accurate ones, moving the call's files |
| `VoiceLibrary.cs` | Known voices (`voices.json`): enroll, match with thresholds and margin, forget |
| `VoicesForm.cs` | List of known voices |
| `CallAssistant.cs` | Scheduling, prompts, notes, cost |
| `LlmClient.cs` | OpenAI-compatible streaming chat for both providers, model lists, starting the LM Studio server |
| `CallRecorder.cs`, `CallTracks.cs` | Capture, mixing, MP3; temporary 16 kHz tracks |
| `LiveTranscriber.cs`, `FileTranscriber.cs`, `SpeechEngine.cs`, `ModelStore.cs`, `VulkanGpu.cs` | Speech recognition and models |
| `AppSettings.cs` | `settings.json`, projects, DPAPI-protected secrets, log |
| `Strings.cs` | All UI text in three languages |

Threading: recognition and LLM requests run on background threads; `CallSession` and `CallAssistant` events fire there. The window never touches controls from those events — it marks what is dirty and redraws from a UI timer. `TrayApp` marshals with `OnUi`.

## Pitfalls

- The laptop has two GPUs; whisper.cpp must get the discrete one (`VulkanGpu`), the integrated Radeon crashes on model load.
- LM Studio's context length is what the model was **loaded** with (often 8192), not its maximum; the transcript sent is limited by it (`AssistantSettings.LocalContext`).
- OpenRouter's last streamed chunk carries `usage` with an empty `choices` array.
- Voice-print similarities on compressed call audio are high even between different people (up to ~0.76, see `VoiceLibrary.FinalThreshold`). Re-measure on real calls before lowering the thresholds; a wrong name is worse than none.
- A call's files are moved only when nothing writes to them (state Done/Failed); all writes to them go through `CallSession` under its lock (`Add`, `WriteFile`, `AppendNotes`, `MoveTo`).
- `voices.json` holds biometric data: never commit it, log it or send it anywhere.
- Editing Cyrillic or Swedish text through PowerShell can corrupt it (code page); use the file tools or Bash.
- Never log or commit the API key; it is stored only DPAPI-encrypted.

## Conventions

- Commit messages: one English subject line.
- Keep README and SPEC in sync with behaviour changes, in English.
- Docker is not used.
