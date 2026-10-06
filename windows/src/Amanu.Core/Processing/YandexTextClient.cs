using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Amanu.Core.Configuration;

namespace Amanu.Core.Processing;

/// <summary>Explicit, stateless Yandex text egress. No redirects or automatic retries.</summary>
public static class YandexTextClient
{
    public static readonly Uri Endpoint = new("https://ai.api.cloud.yandex.net/foundationModels/v1/completion");
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(5) };

    public static Task<string> AskAsync(string key, YandexCloudSettings settings, string system,
        string prompt, CancellationToken token) => AskResumableAsync(Client,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Amanu SmartSpace Data", "yandex-requests"), key, settings, system, prompt, token);

    public static async Task<string> AskResumableAsync(HttpClient client, string directory, string key,
        YandexCloudSettings settings, string system, string prompt, CancellationToken token)
    {
        Validate(key, settings);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { settings.FolderId, settings.Model, settings.MaxOutputTokens, system, prompt }))));
        var answerPath = Path.Combine(directory, fingerprint + ".answer.txt");
        var pendingPath = Path.Combine(directory, fingerprint + ".pending.json");
        if (File.Exists(answerPath)) return await File.ReadAllTextAsync(answerPath, token).ConfigureAwait(false);
        try
        {
            using var marker = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(marker, new { submittedAt = DateTimeOffset.UtcNow });
            marker.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(pendingPath))
        {
            throw new ProcessingFailure(FailureKind.Environmental,
                "Yandex Cloud: исход предыдущей генерации неизвестен. Автоматический повтор заблокирован; проверьте запрос перед удалением маркера " + pendingPath);
        }
        try
        {
            var answer = await AskAsync(client, key, settings, system, prompt, token).ConfigureAwait(false);
            var temporary = answerPath + ".tmp";
            await File.WriteAllTextAsync(temporary, answer, CancellationToken.None).ConfigureAwait(false);
            File.Move(temporary, answerPath, overwrite: true);
            File.Delete(pendingPath);
            return answer;
        }
        catch (ProcessingFailure failure) when (failure.Data.Contains("DefiniteRejection"))
        {
            File.Delete(pendingPath);
            throw;
        }
    }

    private static void Validate(string key, YandexCloudSettings settings)
    {
        if (string.IsNullOrWhiteSpace(key) || !Regex.IsMatch(settings.FolderId, @"\A[a-zA-Z0-9_-]{1,100}\z")
            || !Regex.IsMatch(settings.Model, @"\A[a-zA-Z0-9_-]+/[a-zA-Z0-9_.-]+\z")
            || settings.MaxOutputTokens is < 1 or > 32000)
            throw new ProcessingFailure(FailureKind.Environmental, "Yandex Cloud: проверьте ключ, Folder ID, модель и лимит ответа.");
    }

    // Injected client is for contract tests; production always uses the no-redirect client above.
    public static async Task<string> AskAsync(HttpClient client, string key, YandexCloudSettings settings,
        string system, string prompt, CancellationToken token)
    {
        Validate(key, settings);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Api-Key", key);
        request.Headers.Add("x-data-logging-enabled", "false");
        request.Content = JsonContent.Create(new
        {
            modelUri = $"gpt://{settings.FolderId}/{settings.Model}",
            completionOptions = new { stream = false, temperature = 0.1,
                maxTokens = settings.MaxOutputTokens.ToString(CultureInfo.InvariantCulture) },
            messages = new[] { new { role = "system", text = system }, new { role = "user", text = prompt } },
        });
        try
        {
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            // Never expose an error body: it can echo credentials or meeting content.
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var failure = ProcessingFailure.FromHttp("Yandex Cloud", status, null);
                if (status is >= 400 and < 500 and not 408) failure.Data["DefiniteRejection"] = true;
                throw failure;
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var alternative = json.RootElement.GetProperty("result").GetProperty("alternatives")[0];
            if (alternative.GetProperty("status").GetString() != "ALTERNATIVE_STATUS_FINAL")
                throw new ProcessingFailure(FailureKind.Recording, "Yandex Cloud: ответ не завершён. Увеличьте лимит ответа или сократите текст.");
            var text = alternative.GetProperty("message").GetProperty("text").GetString();
            if (string.IsNullOrWhiteSpace(text))
                throw new ProcessingFailure(FailureKind.Recording, "Yandex Cloud: пустой ответ.");
            return text;
        }
        catch (HttpRequestException)
        {
            throw ProcessingFailure.FromHttp("Yandex Cloud", null, null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new ProcessingFailure(FailureKind.Transient, "Yandex Cloud: время ожидания истекло; запрос мог быть обработан и оплачен.");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new ProcessingFailure(FailureKind.Recording, "Yandex Cloud: неожиданный формат ответа.");
        }
    }
}

public static class MeetingProtocol
{
    public const string Template = """
        Составь краткий протокол совещания на русском. Используй только сведения из расшифровки.
        Структура: Обсуждение по объектам; Принятые решения; Задачи (задача, ответственный,
        срок); Открытые вопросы.
        В разделе «Задачи» используй Markdown-таблицу с колонками:
        Объект | Задача | Ответственный | Срок | Цитата | Таймкод.
        Цитата — дословная реплика, подтверждающая задачу; таймкод — её время из расшифровки.
        Если точной цитаты или таймкода нет, поставь «не указан». Объединяй повторы. Не превращай предложения в принятые решения.
        Если ответственный, срок или объект не названы — напиши «не указан».
        Не приписывай человеку слова по обращению к нему. Сохраняй технические обозначения.
        Не исправляй исходную расшифровку и не добавляй выдуманные факты.
        """;
}
