using System.Net;
using System.Net.Http;
using System.Text.Json;
using Amanu.App;
using Amanu.Core.Configuration;
using Amanu.Core.Processing;
using Xunit;

namespace Amanu.Live.Tests;

public sealed class SpeechKitTests
{
    [Theory]
    [InlineData("A")]
    [InlineData("them")]
    [InlineData("me")]
    public async Task Aggregate_channel_never_gets_one_participants_name(string label)
    {
        var directory = Path.Combine(Path.GetTempPath(), "amanu-names-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("No cloud naming allowed")));
            var settings = new AppSettings { UserName = "Игорь", Summary = new() { Backend = "yandex" } };
            var models = new LanguageModels(client, new SecretStore(), () => settings);
            var service = new SpeakerNamingService(models, () => settings);
            var transcript = new TranscriptDocument("speechkit", "general", DateTimeOffset.UtcNow,
                [new(0, 1000, "Меня зовут Сергей.", label), new(1000, 2000, "А я отвечаю за другую часть.", label)]);
            var result = await service.ResolveAsync(directory, transcript, default);
            Assert.Empty(result.Names);
            Assert.False(result.AskedModel);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    [Fact]
    public void Language_codes_map_before_submission_and_auto_stays_auto()
    {
        Assert.Equal(new[] { "ru-RU", "en-US" }, SpeechKitTranscriptionEngine.Languages("ru"));
        Assert.Equal(new[] { "auto" }, SpeechKitTranscriptionEngine.Languages(null));
        Assert.Throws<ProcessingFailure>(() => SpeechKitTranscriptionEngine.Languages("be"));
    }

    [Fact]
    public async Task Definitely_rejected_submission_can_recover_after_key_change()
    {
        var directory = Path.Combine(Path.GetTempPath(), "amanu-speechkit-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.Unauthorized)));
            var engine = new SpeechKitTranscriptionEngine("bad-key", new() { FolderId = "folder" }, "ru", client);
            var path = Path.Combine(directory, "job.json");
            await Assert.ThrowsAsync<ProcessingFailure>(() => engine.RecognizeChunkAsync([1], path, default));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
    private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

    [Fact]
    public async Task Restart_resumes_operation_without_another_paid_post()
    {
        var directory = Path.Combine(Path.GetTempPath(), "amanu-speechkit-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var calls = new List<string>();
            var finished = false;
            using var client = new HttpClient(new Handler(request =>
            {
                calls.Add(request.Method + " " + request.RequestUri!.Host);
                Assert.Equal("test-key", request.Headers.Authorization!.Parameter);
                Assert.Equal("false", Assert.Single(request.Headers.GetValues("x-data-logging-enabled")));
                if (request.Method == HttpMethod.Post) return Ok("{\"id\":\"operation-id\"}");
                if (request.RequestUri.Host == "operation.api.cloud.yandex.net")
                    return Ok(finished ? "{\"done\":true}" : "{\"done\":false}");
                return Ok("result");
            }));
            var path = Path.Combine(directory, "job.json");
            var engine = new SpeechKitTranscriptionEngine("test-key", new() { FolderId = "folder" }, "ru-RU", client);
            await Assert.ThrowsAsync<ProcessingFailure>(() => engine.RecognizeChunkAsync([1, 2, 3], path, default));
            finished = true;
            var restarted = new SpeechKitTranscriptionEngine("test-key", new() { FolderId = "folder" }, "ru-RU", client);
            Assert.Equal("result", await restarted.RecognizeChunkAsync([1, 2, 3], path, default));
            Assert.Single(calls, call => call.StartsWith("POST"));
            Assert.DoesNotContain("test-key", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Uncertain_submission_does_not_repeat()
    {
        var directory = Path.Combine(Path.GetTempPath(), "amanu-speechkit-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var calls = 0;
            using var client = new HttpClient(new Handler(_ => { calls++; throw new HttpRequestException("secret"); }));
            var engine = new SpeechKitTranscriptionEngine("test-key", new() { FolderId = "folder" }, "ru-RU", client);
            var path = Path.Combine(directory, "job.json");
            await Assert.ThrowsAsync<ProcessingFailure>(() => engine.RecognizeChunkAsync([1], path, default));
            var error = await Assert.ThrowsAsync<ProcessingFailure>(() => engine.RecognizeChunkAsync([1], path, default));
            Assert.Equal(FailureKind.Environmental, error.Kind);
            Assert.Equal(1, calls);
            Assert.DoesNotContain("secret", error.Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Normalization_replaces_final_without_duplicating_and_keeps_channel_and_offset()
    {
        const string json = """
            {"result":{"channelTag":"0","audioCursors":{"finalIndex":"1"},"final":{"alternatives":[{"text":"one","startTimeMs":"100","endTimeMs":"200"}]}}}
            {"result":{"channelTag":"0","finalRefinement":{"finalIndex":"1","normalizedText":{"alternatives":[{"text":"1","startTimeMs":"100","endTimeMs":"200"}]}}}}
            {"result":{"channelTag":"1","audioCursors":{"finalIndex":"1"},"final":{"alternatives":[{"text":"other","startTimeMs":"300","endTimeMs":"400"}]}}}
            """;
        var segments = SpeakerLabels.Assign(SpeechKitTranscriptionEngine.Parse(json, true, "", 500));
        Assert.Equal(2, segments.Count);
        Assert.Equal("1", segments[0].Text);
        Assert.Equal(600, segments[0].StartMs);
        Assert.Equal(SpeakerLabels.Me, segments[0].Speaker);
        Assert.Equal(SpeakerLabels.Them, segments[1].Speaker);
    }
}
