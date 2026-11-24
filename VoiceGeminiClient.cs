using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GeminiSidekick;

internal sealed class WavMicRecorder : IDisposable
{
    private const string Alias = "GeminiSidekickMic";
    private bool _recording;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendStringW(string command, StringBuilder? returnString, int returnLength, IntPtr hwndCallback);

    public bool IsRecording => _recording;

    public void Start()
    {
        if (_recording) return;

        mciSendStringW($"close {Alias}", null, 0, IntPtr.Zero);
        Send($"open new type waveaudio alias {Alias}");
        Send($"set {Alias} time format ms bitspersample 16 channels 1 samplespersec 16000 bytespersec 32000 alignment 2");
        Send($"record {Alias}");
        _recording = true;
    }

    public string StopAndSaveWav()
    {
        if (!_recording)
            throw new InvalidOperationException("Запись микрофона не запущена.");

        _recording = false;
        var wavPath = Path.Combine(Path.GetTempPath(), $"geminisidekick_{DateTime.Now:HHmmss_fff}.wav");
        try
        {
            Send($"stop {Alias}");
            Send($"save {Alias} \"{wavPath}\"");
        }
        finally
        {
            mciSendStringW($"close {Alias}", null, 0, IntPtr.Zero);
        }

        if (!File.Exists(wavPath) || new FileInfo(wavPath).Length < 64)
            throw new InvalidOperationException("Аудиофайл не записался. Проверьте микрофон в Windows.");

        return wavPath;
    }

    private static void Send(string command)
    {
        var err = new StringBuilder(256);
        var code = mciSendStringW(command, null, 0, IntPtr.Zero);
        if (code != 0)
        {
            mciSendStringW($"strerror {code}", err, err.Capacity, IntPtr.Zero);
            throw new InvalidOperationException($"MCI ({code}) на '{command}': {err}");
        }
    }

    public void Dispose()
    {
        if (_recording)
        {
            try { mciSendStringW($"stop {Alias}", null, 0, IntPtr.Zero); } catch { }
            _recording = false;
        }
        try { mciSendStringW($"close {Alias}", null, 0, IntPtr.Zero); } catch { }
    }
}

internal static class GeminiAudioTranscriber
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<string> TranscribeRussianWavAsync(
        string wavPath,
        string model = "gemini-3.1-flash-lite",
        CancellationToken ct = default)
    {
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Не задана переменная окружения GEMINI_API_KEY.");

        var bytes = await File.ReadAllBytesAsync(wavPath, ct);
        var base64Audio = Convert.ToBase64String(bytes);

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = "Распознай русскую речь на аудио и верни только распознанный текст без пояснений и кавычек." },
                        new
                        {
                            inline_data = new
                            {
                                mime_type = "audio/wav",
                                data = base64Audio
                            }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var models = new[]
            {
                model,
                "gemini-3.1-flash-lite",
                "gemini-flash-latest"
            }
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string lastError = "Не удалось получить ответ от Gemini API.";

        foreach (var candidateModel in models)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(candidateModel)}:generateContent?key={Uri.EscapeDataString(apiKey.Trim())}";
            int[] delaysMs = { 0 };

            for (int attempt = 0; attempt < delaysMs.Length; attempt++)
            {
                if (delaysMs[attempt] > 0)
                    await Task.Delay(delaysMs[attempt], ct);

                try
                {
                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attemptCts.CancelAfter(TimeSpan.FromSeconds(25));

                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    using var resp = await Http.SendAsync(req, attemptCts.Token);
                    var statusCode = (int)resp.StatusCode;
                    var body = await resp.Content.ReadAsStringAsync(attemptCts.Token);

                    if (resp.IsSuccessStatusCode)
                        try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
                        return ExtractText(body);

                    lastError = $"Gemini API {statusCode} ({candidateModel})";

                    if (statusCode == 503 || statusCode == 429 || statusCode == 404)
                        continue;

                    throw new HttpRequestException(lastError);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastError = $"Таймаут ответа от {candidateModel}";
                }
                catch (HttpRequestException ex)
                {
                    lastError = $"Сбой сети ({candidateModel}): {ex.Message}";
                }
            }
        }

        throw new HttpRequestException(lastError);
    }

    public sealed record AssistantDecision(string Action, string Target, string Reply);

    public static async Task<AssistantDecision> DecideAndReplyAsync(
        string userText,
        IReadOnlyList<string> availableTargets,
        CancellationToken ct = default)
    {
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return new AssistantDecision("reply", string.Empty, $"Не задан GEMINI_API_KEY, но запрос «{userText}» получен.");

        var targetsSample = string.Join("\n", availableTargets.Take(80));
        var prompt =
            "Ты голосовой помощник GeminiSidekick на Windows. Пользователь сказал или ввёл запрос.\n" +
            "Твоя задача: решить, нужно ли запустить программу/игру/приложение (action=\"launch\"), вставить текст в активное окно (action=\"paste\") " +
            "или просто ответить на вопрос/разговор (action=\"reply\").\n" +
            "Если нужно запустить приложение, выбери наиболее подходящую цель из списка доступных программ/ярлыков или укажи точное название/команду.\n" +
            "Поле \"reply\" должно быть живым, естественным и разнообразным ответом на русском языке (без сухих шаблонов вроде 'Открываю ...' каждый раз, но по делу и кратко).\n" +
            "Верни ТОЛЬКО валидный JSON без markdown-оформления в формате:\n" +
            "{\"action\":\"launch|paste|reply\",\"target\":\"...\",\"reply\":\"...\"}\n\n" +
            $"Запрос пользователя: {userText}\n\n" +
            $"Доступные приложения/ярлыки на ПК:\n{targetsSample}";

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var models = new[]
            {
                "gemini-3.1-flash-lite",
                "gemini-flash-latest"
            }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string lastError = "Ошибка обращения к Gemini.";

        foreach (var candidateModel in models)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(candidateModel)}:generateContent?key={Uri.EscapeDataString(apiKey.Trim())}";
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(25));

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var resp = await Http.SendAsync(req, attemptCts.Token);
                var statusCode = (int)resp.StatusCode;
                var body = await resp.Content.ReadAsStringAsync(attemptCts.Token);

                if (resp.IsSuccessStatusCode)
                {
                    var rawReply = ExtractText(body).Trim();
                    var cleaned = rawReply.Trim();
                    if (cleaned.StartsWith("`", StringComparison.Ordinal))
                    {
                        var firstNl = cleaned.IndexOf('\n');
                        if (firstNl >= 0) cleaned = cleaned[(firstNl + 1)..];
                        var lastFence = cleaned.LastIndexOf("`", StringComparison.Ordinal);
                        if (lastFence >= 0) cleaned = cleaned[..lastFence];
                        cleaned = cleaned.Trim();
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(cleaned);
                        var root = doc.RootElement;
                        var action = root.TryGetProperty("action", out var aProp) ? (aProp.GetString() ?? "reply") : "reply";
                        var target = root.TryGetProperty("target", out var tProp) ? (tProp.GetString() ?? string.Empty) : string.Empty;
                        var reply = root.TryGetProperty("reply", out var rProp) ? (rProp.GetString() ?? string.Empty) : string.Empty;
                        if (!string.IsNullOrWhiteSpace(reply))
                            return new AssistantDecision(action.ToLowerInvariant(), target, reply);
                    }
                    catch
                    {
                        // Если модель вернула обычный текст вместо JSON — отдаём его как ответ
                    }

                    if (!string.IsNullOrWhiteSpace(rawReply))
                        return new AssistantDecision("reply", string.Empty, rawReply);
                }

                lastError = $"Gemini API {statusCode} ({candidateModel})";
                if (statusCode == 503 || statusCode == 429 || statusCode == 404)
                    continue;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        return new AssistantDecision("reply", string.Empty, $"Сейчас не удалось связаться с Gemini ({lastError}).");
    }

    private static string ExtractText(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
            candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("content", out var content) &&
            content.TryGetProperty("parts", out var parts) &&
            parts.GetArrayLength() > 0 &&
            parts[0].TryGetProperty("text", out var textProp))
        {
            return (textProp.GetString() ?? string.Empty).Trim();
        }

        return string.Empty;
    }
}
