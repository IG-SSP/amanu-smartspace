using System.Net;
using System.Text;
using System.Text.Json;
using Amanu.Core.Configuration;
using Amanu.Core.Processing;

namespace Amanu.Core.Tests;

public sealed class YandexTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }

    [Fact]
    public async Task Request_is_private_scoped_and_stateless()
    {
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(YandexTextClient.Endpoint, request.RequestUri);
            Assert.Equal("Api-Key", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            Assert.Equal("false", Assert.Single(request.Headers.GetValues("x-data-logging-enabled")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("gpt://folder/yandexgpt/latest", json.RootElement.GetProperty("modelUri").GetString());
            Assert.Equal("4000", json.RootElement.GetProperty("completionOptions").GetProperty("maxTokens").GetString());
            Assert.Equal("meeting", json.RootElement.GetProperty("messages")[1].GetProperty("text").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent("""
                {"result":{"alternatives":[{"status":"ALTERNATIVE_STATUS_FINAL","message":{"text":"note"}}]}}
                """, Encoding.UTF8, "application/json") };
        }));
        Assert.Equal("note", await YandexTextClient.AskAsync(client, "test-key",
            new() { FolderId = "folder" }, "system", "meeting", default));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Failure_does_not_disclose_response_or_retry(int status)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
                { Content = new StringContent("test-key and private meeting") });
        }));
        var error = await Assert.ThrowsAsync<ProcessingFailure>(() => YandexTextClient.AskAsync(client,
            "test-key", new() { FolderId = "folder" }, "system", "meeting", default));
        Assert.DoesNotContain("test-key", error.Message);
        Assert.DoesNotContain("private meeting", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Yandex_is_explicit_and_never_an_auto_fallback()
    {
        Assert.Equal(new[] { "yandex" }, LanguageModelChain.Allowed("yandex", _ => true));
        Assert.DoesNotContain("yandex", LanguageModelChain.Allowed("auto", _ => true));
    }

    [Theory]
    [InlineData("Меня зовут Игорь.", "Игорь")]
    [InlineData("Я Сергей, отвечаю за монтаж.", "Сергей")]
    [InlineData("Игорь, проверь свет.", null)]
    [InlineData("Это Игорь сказал вчера.", null)]
    [InlineData("Я думаю, что Игорь прав.", null)]
    public void Only_self_introduction_names_a_speaker(string text, string? expected) =>
        Assert.Equal(expected, SelfIntroduction.Name(text));

    [Fact]
    public void Ambiguous_voice_and_other_speakers_quote_are_not_named()
    {
        Assert.Null(SelfIntroduction.UniqueName(["Меня зовут Игорь.", "Меня зовут Сергей."]));
        Assert.False(SelfIntroduction.Supports("Игорь", "Меня зовут Игорь.", ["Игорь, проверь свет."]));
        Assert.False(SelfIntroduction.Supports("Игорь", "Меня зовут Игорь.", ["Он сказал: «Меня зовут Игорь.»"]));
    }

    [Fact]
    public async Task Uncertain_generation_is_blocked_across_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "amanu-yandex-test-" + Guid.NewGuid());
        try
        {
            var calls = 0;
            using var client = new HttpClient(new Handler(_ => { calls++; throw new HttpRequestException("secret"); }));
            await Assert.ThrowsAsync<ProcessingFailure>(() => YandexTextClient.AskResumableAsync(client,
                directory, "test-key", new() { FolderId = "folder" }, "system", "meeting", default));
            await Assert.ThrowsAsync<ProcessingFailure>(() => YandexTextClient.AskResumableAsync(client,
                directory, "rotated-key", new() { FolderId = "folder" }, "system", "meeting", default));
            Assert.Equal(1, calls);
            Assert.DoesNotContain("test-key", File.ReadAllText(Assert.Single(Directory.GetFiles(directory))));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
