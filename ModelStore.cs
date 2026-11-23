namespace AiCallAssistant;

/// <summary>Speech models live in the "models" folder next to the exe and are downloaded on first use.</summary>
static class ModelStore
{
    public static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "models");

    sealed record Model(string Title, string FileName, string Url);

    static readonly Model WhisperModel = new("Whisper large-v3-turbo", "ggml-large-v3-turbo-q8_0.bin",
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q8_0.bin");
    static readonly Model SegmentationModel = new("pyannote segmentation", "pyannote-segmentation-3-0.onnx",
        "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx");
    static readonly Model EmbeddingModel = new("speaker embedding", "wespeaker_en_voxceleb_resnet34_LM.onnx",
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx");
    static readonly Model VadModel = new("silero VAD", "silero_vad.onnx",
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx");

    static readonly Model[] All = { VadModel, SegmentationModel, EmbeddingModel, WhisperModel };

    public static string Whisper => PathOf(WhisperModel);
    public static string Segmentation => PathOf(SegmentationModel);
    public static string Embedding => PathOf(EmbeddingModel);
    public static string Vad => PathOf(VadModel);

    static string PathOf(Model m) => Path.Combine(Dir, m.FileName);

    public static async Task DownloadMissingAsync(Action<string?, double?> status)
    {
        var missing = All.Where(m => !File.Exists(PathOf(m))).ToList();
        if (missing.Count == 0)
            return;

        Directory.CreateDirectory(Dir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AiCallAssistant/1.0");

        foreach (var m in missing)
        {
            var target = PathOf(m);
            var part = target + ".part";
            AppLog.Write($"Downloading {m.Url}");

            using (var response = await http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1;
                await using var src = await response.Content.ReadAsStreamAsync();
                await using var dst = File.Create(part);

                var buffer = new byte[1 << 20];
                long done = 0;
                int lastPct = -1, read;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    int pct = total > 0 ? (int)(done * 100 / total) : -1;
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        status(pct >= 0
                                ? Strings.Current.Downloading(m.Title, pct, total / 1048576)
                                : Strings.Current.DownloadingNoTotal(m.Title, done / 1048576),
                            pct >= 0 ? pct / 100.0 : null);
                    }
                }
            }

            File.Move(part, target, overwrite: true);
        }

        status(null, null);
    }
}
