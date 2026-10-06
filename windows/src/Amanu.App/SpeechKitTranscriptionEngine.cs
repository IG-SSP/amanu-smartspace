using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amanu.Core.Configuration;
using Amanu.Core.Processing;

namespace Amanu.App;

/// <summary>Resumable asynchronous recognition without S3, functions or a VM.</summary>
public sealed class SpeechKitTranscriptionEngine(string key, YandexCloudSettings config, string? language,
    HttpClient? testClient = null) : ITranscriptionEngine
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(5) };
    public string Name => "speechkit";
    public sealed record Job(string? Id, DateTimeOffset SubmittedAt);

    public static IReadOnlyList<string> Languages(string? language)
    {
        var code = language?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(code) || code == "auto") return ["auto"];
        var mapped = code switch
        {
            "ru" or "ru-ru" => "ru-RU", "en" or "en-us" => "en-US",
            "de" or "de-de" => "de-DE", "es" or "es-es" => "es-ES",
            "fr" or "fr-fr" => "fr-FR", "fi" or "fi-fi" => "fi-FI",
            "it" or "it-it" => "it-IT", "nl" or "nl-nl" => "nl-NL",
            "pl" or "pl-pl" => "pl-PL", "pt" or "pt-pt" => "pt-PT",
            "sv" or "sv-se" => "sv-SE",
            _ => throw new ProcessingFailure(FailureKind.Environmental, "SpeechKit: выбранный язык не поддерживается. Выберите автоматическое определение или другой движок."),
        };
        return mapped == "en-US" ? [mapped] : [mapped, "en-US"];
    }

    public async Task<TranscriptDocument> TranscribeAsync(SessionAudio audio, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(config.FolderId) || config.SpeechKitModel is not ("general" or "deferred-general"))
            throw new ProcessingFailure(FailureKind.Environmental, "SpeechKit: укажите Folder ID и модель в Дополнительно → Yandex Cloud.");
        var temporary = Path.Combine(audio.Directory, ".speechkit-input");
        Directory.CreateDirectory(temporary);
        try
        {
            var wave = Path.Combine(temporary, "input.wav");
            if (audio.BothSides)
                AudioPreprocessor.CreateAlignedStereo16k(audio.Microphone!, audio.System!,
                    audio.MicrophoneOffsetMs, audio.SystemOffsetMs, wave);
            else AudioPreprocessor.ConvertToMono16k(audio.Only.Path, wave);
            var shift = audio.BothSides ? Math.Min(audio.MicrophoneOffsetMs, audio.SystemOffsetMs) : audio.Only.OffsetMs;
            var partsDirectory = Path.Combine(temporary, "parts");
            Directory.CreateDirectory(partsDirectory);
            // Ten minutes of 16-bit 16 kHz stereo stays below 40 MB before base64.
            var parts = AudioPreprocessor.SplitWav(wave, partsDirectory, minutes: 10);
            var segments = new List<TranscriptSegment>();
            for (var index = 0; index < parts.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var part = parts[index];
                var bytes = await File.ReadAllBytesAsync(part.Path, token).ConfigureAwait(false);
                if (bytes.Length > 40_000_000)
                    throw new ProcessingFailure(FailureKind.Recording, "SpeechKit: слишком большой фрагмент аудио.");
                var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"v1|{config.FolderId}|{config.SpeechKitModel}|{language}|{audio.BothSides}|{index}|" +
                    Convert.ToHexString(SHA256.HashData(bytes)))));
                var prefix = Path.Combine(audio.Directory, $".speechkit-{fingerprint}");
                var resultPath = prefix + ".result.ndjson";
                string result;
                if (File.Exists(resultPath)) result = await File.ReadAllTextAsync(resultPath, token).ConfigureAwait(false);
                else
                {
                    result = await RecognizeChunkAsync(bytes, prefix + ".job.json", token).ConfigureAwait(false);
                    await AtomicFiles.WriteTextAsync(resultPath, result, token).ConfigureAwait(false);
                }
                segments.AddRange(Parse(result, audio.BothSides, audio.BothSides ? "" : audio.Only.Side,
                    shift + (long)Math.Round(part.OffsetSeconds * 1000)));
            }
            return new(Name, config.SpeechKitModel, DateTimeOffset.UtcNow,
                SpeakerLabels.Assign(segments.OrderBy(segment => segment.StartMs).ToArray()));
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    public async Task<string> RecognizeChunkAsync(byte[] bytes, string jobPath, CancellationToken token)
    {
        var languages = Languages(language); // Reject invalid UI language before any submission marker.
        Job? job = File.Exists(jobPath)
            ? JsonSerializer.Deserialize<Job>(await File.ReadAllTextAsync(jobPath, token), AtomicFiles.JsonOptions) : null;
        if (job is { Id: null })
            throw new ProcessingFailure(FailureKind.Environmental,
                "SpeechKit: отправка могла состояться, но ID не получен. Проверьте облачную операцию перед повторной отправкой; файл " + Path.GetFileName(jobPath));
        if (job is null)
        {
            token.ThrowIfCancellationRequested();
            // Written before POST. A crash/timeout cannot cause a silent duplicate paid submission.
            job = new(null, DateTimeOffset.UtcNow);
            await AtomicFiles.WriteJsonAsync(jobPath, job, token).ConfigureAwait(false);
            using var request = Request(HttpMethod.Post, "https://stt.api.cloud.yandex.net/stt/v3/recognizeFileAsync");
            request.Content = JsonContent.Create(new
            {
                content = Convert.ToBase64String(bytes),
                recognitionModel = new
                {
                    model = config.SpeechKitModel,
                    audioFormat = new { containerAudio = new { containerAudioType = "WAV" } },
                    languageRestriction = new { restrictionType = "WHITELIST", languageCode = languages },
                    textNormalization = new { textNormalization = "TEXT_NORMALIZATION_ENABLED", literatureText = false },
                },
            });
            string response;
            try { response = await SendAsync(request, token).ConfigureAwait(false); }
            catch (ProcessingFailure failure) when (failure.Data.Contains("DefiniteRejection"))
            {
                File.Delete(jobPath);
                throw;
            }
            using var json = JsonDocument.Parse(response);
            var id = json.RootElement.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id)) throw new ProcessingFailure(FailureKind.Environmental, "SpeechKit: ID операции отсутствует; проверьте отправку.");
            job = job with { Id = id };
            // Once we have the ID, cancellation must not lose it.
            await AtomicFiles.WriteJsonAsync(jobPath, job, CancellationToken.None).ConfigureAwait(false);
        }
        using var poll = Request(HttpMethod.Get, "https://operation.api.cloud.yandex.net/operations/" + Uri.EscapeDataString(job.Id!));
        using var state = JsonDocument.Parse(await SendAsync(poll, token).ConfigureAwait(false));
        if (!state.RootElement.TryGetProperty("done", out var done) || !done.GetBoolean())
            throw new ProcessingFailure(FailureKind.Transient, "SpeechKit: операция выполняется. Продолжим по сохранённому ID без повторной отправки.");
        if (state.RootElement.TryGetProperty("error", out _))
            throw new ProcessingFailure(FailureKind.Recording, "SpeechKit: облачная операция завершилась ошибкой. ID сохранён для проверки.");
        using var get = Request(HttpMethod.Get, "https://stt.api.cloud.yandex.net/stt/v3/getRecognition?operationId=" + Uri.EscapeDataString(job.Id!));
        return await SendAsync(get, token).ConfigureAwait(false);
    }

    private HttpRequestMessage Request(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Api-Key", key);
        request.Headers.Add("x-folder-id", config.FolderId);
        request.Headers.Add("x-data-logging-enabled", "false");
        return request;
    }

    private async Task<string> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        try
        {
            using var response = await (testClient ?? Client).SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var failure = ProcessingFailure.FromHttp("SpeechKit", status, null);
                if (status is >= 400 and < 500 and not 408) failure.Data["DefiniteRejection"] = true;
                throw failure;
            }
            return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        catch (HttpRequestException) { throw ProcessingFailure.FromHttp("SpeechKit", null, null); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new ProcessingFailure(FailureKind.Transient, "SpeechKit: истекло время ожидания; состояние отправки сохранено."); }
    }

    public static IReadOnlyList<TranscriptSegment> Parse(string ndjson, bool stereo, string side, long offset)
    {
        var segments = new Dictionary<(string Channel, string Index), TranscriptSegment>();
        foreach (var line in ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.TryGetProperty("result", out var wrapped)) root = wrapped;
            JsonElement update;
            string index;
            if (root.TryGetProperty("finalRefinement", out var refinement))
            {
                if (!refinement.TryGetProperty("normalizedText", out update)) continue;
                index = refinement.GetProperty("finalIndex").ToString();
            }
            else if (root.TryGetProperty("final", out update))
                index = root.GetProperty("audioCursors").GetProperty("finalIndex").ToString();
            else continue;
            var channel = root.TryGetProperty("channelTag", out var tag) ? tag.ToString() : "";
            // The top-level tag is the audio channel, the update's tag can be a diarized voice.
            if (stereo && channel is not ("0" or "1"))
                throw new ProcessingFailure(FailureKind.Recording, "SpeechKit: неизвестный аудиоканал; имя говорящего не назначено.");
            var alternatives = update.GetProperty("alternatives");
            if (alternatives.GetArrayLength() == 0) continue;
            var alternative = alternatives[0];
            var text = alternative.GetProperty("text").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            var speakerSide = stereo ? channel == "0" ? SpeakerLabels.Me : SpeakerLabels.Them : side;
            // No speaker labeling requested: do not invent identities within one channel.
            segments[(channel, index)] = new(
                offset + Time(alternative, "startTimeMs"),
                offset + Time(alternative, "endTimeMs"),
                text, SpeakerLabels.Raw(speakerSide, ""));
        }
        return segments.Values.OrderBy(segment => segment.StartMs).ToArray();
    }

    private static long Time(JsonElement alternative, string property)
    {
        if (alternative.TryGetProperty(property, out var value))
            return long.Parse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture);
        if (alternative.TryGetProperty("words", out var words) && words.GetArrayLength() > 0)
        {
            var word = property == "startTimeMs" ? words[0] : words[words.GetArrayLength() - 1];
            if (word.TryGetProperty(property, out value))
                return long.Parse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture);
        }
        // Protobuf JSON can omit a zero-valued timestamp.
        return 0;
    }
}
