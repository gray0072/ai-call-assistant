using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiCallAssistant;

/// <summary>A model the user can pick. Prices are USD per million tokens (null — unknown).</summary>
sealed record LlmModel(string Id, string Name, decimal? InputPrice, decimal? OutputPrice, int? Context, bool Loaded, bool IsLocal = false)
{
    public bool Free => InputPrice == 0 && OutputPrice == 0;
}

sealed record ChatMessage(string Role, string Content);

/// <param name="FinishReason">Why the model stopped ("stop", "length", …).</param>
/// <param name="ReasoningChars">Length of the hidden reasoning a thinking model streamed besides the answer.</param>
sealed record ChatResult(string Text, int? InputTokens, int? OutputTokens, decimal? Cost, string? FinishReason = null, int ReasoningChars = 0);

/// <summary>
/// Both LM Studio and OpenRouter speak the OpenAI chat-completions protocol, so one client serves both;
/// only the model lists differ (LM Studio's native API tells which models are loaded,
/// OpenRouter's tells prices and can sort by popularity).
/// </summary>
static class LlmClient
{
    public const string OpenRouterUrl = "https://openrouter.ai/api/v1";
    public const string OpenRouterKeysPage = "https://openrouter.ai/settings/keys";

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AiCallAssistant/1.0");
        // OpenRouter shows this name in its activity log.
        http.DefaultRequestHeaders.Add("X-Title", AppInfo.Name);
        return http;
    }

    // ---------- model lists ----------

    /// <summary>Chat models downloaded in LM Studio: loaded ones first. Starts the LM Studio server if it is not running.</summary>
    public static async Task<List<LlmModel>> ListLocalAsync(string baseUrl, CancellationToken ct = default)
    {
        var json = await WithLocalServerAsync(baseUrl, () => GetJsonAsync($"{Root(baseUrl)}/api/v0/models", null, ct));
        var result = new List<LlmModel>();
        foreach (var m in json["data"]!.AsArray())
        {
            var type = (string?)m!["type"];
            if (type is not ("llm" or "vlm"))
                continue;
            bool loaded = (string?)m["state"] == "loaded";
            int? context = loaded ? (int?)m["loaded_context_length"] ?? (int?)m["max_context_length"] : (int?)m["max_context_length"];
            var id = (string)m["id"]!;
            result.Add(new LlmModel(id, id, null, null, context, loaded, IsLocal: true));
        }
        return result.OrderByDescending(m => m.Loaded).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>OpenRouter chat models, most popular first (the list is public, no key needed).</summary>
    public static async Task<List<LlmModel>> ListCloudAsync(CancellationToken ct = default)
    {
        var json = await GetJsonAsync($"{OpenRouterUrl}/models?sort=most-popular", null, ct);
        var result = new List<LlmModel>();
        foreach (var m in json["data"]!.AsArray())
        {
            var id = (string)m!["id"]!;
            if (id.EndsWith(":batch", StringComparison.Ordinal))
                continue; // batch-only variants cannot answer during a call
            if (m["architecture"]?["output_modalities"] is JsonArray outputs && !outputs.Any(o => (string?)o == "text"))
                continue;
            if (m["endpoints"] is JsonArray endpoints && !endpoints.Any(e => ((string?)e)?.EndsWith("/chat/completions") == true))
                continue;

            result.Add(new LlmModel(id, (string?)m["name"] ?? id,
                PerMillion(m["pricing"]?["prompt"]), PerMillion(m["pricing"]?["completion"]),
                (int?)m["context_length"], false));
        }
        return result;
    }

    static decimal? PerMillion(JsonNode? perToken)
    {
        var s = (string?)perToken;
        return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v * 1_000_000 : null;
    }

    /// <summary>Checks an OpenRouter key; returns spent and limit in USD (limit null — unlimited).</summary>
    public static async Task<(decimal Usage, decimal? Limit)> CheckKeyAsync(string key, CancellationToken ct = default)
    {
        var json = await GetJsonAsync($"{OpenRouterUrl}/key", key, ct);
        var data = json["data"]!;
        return ((decimal?)data["usage"] ?? 0, (decimal?)data["limit"]);
    }

    // ---------- chat ----------

    /// <summary>Streams a chat completion; <paramref name="onText"/> gets the answer piece by piece.</summary>
    public static async Task<ChatResult> ChatAsync(AssistantSettings settings, IEnumerable<ChatMessage> messages,
        Action<string> onText, int maxTokens, CancellationToken ct)
    {
        bool local = settings.Provider == LlmProvider.LmStudio;
        var model = settings.Model ?? throw new InvalidOperationException(Strings.Current.AiNoModel);
        string url = local ? $"{Root(settings.LmStudioUrl)}/v1/chat/completions" : $"{OpenRouterUrl}/chat/completions";
        string? key = local ? null : settings.OpenRouterKey ?? throw new InvalidOperationException(Strings.Current.AiNoKey);

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["stream"] = true,
            ["max_tokens"] = maxTokens,
            ["temperature"] = 0.3,
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        };
        if (!local)
        {
            body["usage"] = new JsonObject { ["include"] = true }; // OpenRouter then reports the cost
            // Reasoning models answer sooner and cheaper with little thinking; it is not shown anyway.
            body["reasoning"] = new JsonObject { ["effort"] = "low", ["exclude"] = true };
        }

        Task<HttpResponseMessage> Send()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            if (key != null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        using var response = local ? await WithLocalServerAsync(settings.LmStudioUrl, Send) : await Send();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(ErrorText(response, await response.Content.ReadAsStringAsync(ct)), null, response.StatusCode);

        var text = new StringBuilder();
        int? input = null, output = null;
        decimal? cost = null;
        string? finish = null;
        int reasoning = 0;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue; // blank separators and ": keep-alive" comments
            var data = line[5..].Trim();
            if (data == "[DONE]")
                break;

            var chunk = JsonNode.Parse(data)!;
            if (chunk["error"] is { } error)
                throw new HttpRequestException((string?)error["message"] ?? error.ToJsonString());

            // The last chunk (usage only) comes with an empty "choices".
            var choice = chunk["choices"] is JsonArray { Count: > 0 } choices ? choices[0] : null;
            var delta = (string?)choice?["delta"]?["content"];
            finish = (string?)choice?["finish_reason"] ?? finish;
            reasoning += ((string?)choice?["delta"]?["reasoning_content"] ?? (string?)choice?["delta"]?["reasoning"])?.Length ?? 0;
            if (!string.IsNullOrEmpty(delta))
            {
                text.Append(delta);
                onText(delta);
            }

            if (chunk["usage"] is JsonObject usage)
            {
                input = (int?)usage["prompt_tokens"] ?? input;
                output = (int?)usage["completion_tokens"] ?? output;
                cost = (decimal?)usage["cost"] ?? cost;
            }
        }

        if (text.Length == 0)
            AppLog.Write($"Empty answer from {model}: finish_reason={finish}, reasoning chars={reasoning}, tokens in/out={input}/{output}");
        return new ChatResult(text.ToString(), input, output, cost, finish, reasoning);
    }

    // ---------- helpers ----------

    static string Root(string baseUrl)
    {
        var url = baseUrl.Trim().TrimEnd('/');
        return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;
    }

    static async Task<JsonNode> GetJsonAsync(string url, string? key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (key != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await Http.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(ErrorText(response, body), null, response.StatusCode);
        return JsonNode.Parse(body)!;
    }

    static string ErrorText(HttpResponseMessage response, string body)
    {
        string? message = null;
        try { message = (string?)JsonNode.Parse(body)?["error"]?["message"] ?? (string?)JsonNode.Parse(body)?["error"]; }
        catch (JsonException) { }
        message ??= body.Length > 300 ? body[..300] : body;
        return $"{(int)response.StatusCode} {response.ReasonPhrase}: {message}".TrimEnd(' ', ':');
    }

    /// <summary>
    /// Runs a request against LM Studio; if nothing listens there, starts its server with the
    /// <c>lms</c> command-line tool (installed with LM Studio) and tries once more.
    /// </summary>
    static async Task<T> WithLocalServerAsync<T>(string baseUrl, Func<Task<T>> request)
    {
        try
        {
            return await request();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == null && IsLocalhost(baseUrl))
        {
            // Connection refused: nothing listens on the port.
            if (!await TryStartServerAsync())
                throw;
        }
        return await request();
    }

    static bool IsLocalhost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host == "localhost");

    static async Task<bool> TryStartServerAsync()
    {
        var lms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "bin", "lms.exe");
        if (!File.Exists(lms))
            return false;
        try
        {
            AppLog.Write("LM Studio server is not reachable, starting it with lms");
            using var process = Process.Start(new ProcessStartInfo(lms, "server start")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(cts.Token);
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppLog.Write("Starting the LM Studio server failed: " + ex.Message);
            return false;
        }
    }
}
