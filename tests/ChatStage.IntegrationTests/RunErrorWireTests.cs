using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Client;

namespace ChatStage.IntegrationTests;

/// <summary>
/// A failed run over the raw AG-UI wire: an exception the app describes reaches the client as <c>RUN_ERROR</c>'s message,
/// any other stays on the server, and the run still ends with <c>RUN_ERROR</c> as its last event.
/// </summary>
public sealed class RunErrorWireTests : IAsyncLifetime
{
    private AgentServer _server = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await AgentServer.StartAsync();
        _http = _server.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<string> PostAsync(string thread, string text)
    {
        var ct = TestContext.Current.CancellationToken;
        var body = JsonSerializer.Serialize(new { threadId = thread, runId = "r1", messages = new[] { new { id = "m1", role = "user", content = text } } });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agent") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static async Task<List<BaseEvent>> EventsAsync(string sse)
    {
        using var response = new HttpResponseMessage { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        var events = new List<BaseEvent>();
        await foreach (var e in response.ReadAGUIEventStreamAsync(TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task ADescribedExceptionReachesTheClientAsTheRunErrorMessage()
    {
        var events = await EventsAsync(await PostAsync("t-user", "fail: user-facing"));

        Assert.IsType<RunStartedEvent>(events[0]);
        var error = Assert.IsType<RunErrorEvent>(events[^1]);
        Assert.Equal(AgentServer.UserFacingMessage, error.Message);
        Assert.Single(events.OfType<RunErrorEvent>());
        Assert.DoesNotContain(events, e => e is RunFinishedEvent);
    }

    [Fact]
    public async Task AnUndescribedExceptionSendsOnlyTheGenericMessage()
    {
        var sse = await PostAsync("t-internal", "fail: internal");

        var error = Assert.IsType<RunErrorEvent>((await EventsAsync(sse))[^1]);
        Assert.Equal(AgentServer.GenericError, error.Message);
        Assert.DoesNotContain("hunter2", sse, StringComparison.Ordinal);
        Assert.DoesNotContain("db01", sse, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), sse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StateTheAgentSetBeforeADescribedFailureIsSavedForTheNextRun()
    {
        var failed = await EventsAsync(await PostAsync("t-note", "fail: with note"));
        Assert.Equal(AgentServer.UserFacingMessage, Assert.IsType<RunErrorEvent>(failed[^1]).Message);

        var next = await EventsAsync(await PostAsync("t-note", "what's the note?"));

        Assert.Equal(AgentServer.NoteText, string.Concat(next.OfType<TextMessageContentEvent>().Select(t => t.Delta)));
    }

    [Fact]
    public async Task TheSessionIsSavedBeforeTheClientHearsTheRunFailed()
    {
        // Like ChatStage's chat, stop reading at RUN_ERROR and disconnect: the save must already have happened.
        var ct = TestContext.Current.CancellationToken;
        var body = JsonSerializer.Serialize(new { threadId = "t-early", runId = "r1", messages = new[] { new { id = "m1", role = "user", content = "fail: with note" } } });
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/agent") { Content = new StringContent(body, Encoding.UTF8, "application/json") })
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            await foreach (var e in response.ReadAGUIEventStreamAsync(ct))
            {
                if (e is RunErrorEvent)
                {
                    break;
                }
            }
        }

        var next = await EventsAsync(await PostAsync("t-early", "what's the note?"));

        Assert.Equal(AgentServer.NoteText, string.Concat(next.OfType<TextMessageContentEvent>().Select(t => t.Delta)));
    }

    [Fact]
    public async Task TheThreadCarriesOnAfterAFailedRun()
    {
        await PostAsync("t-after", "fail: user-facing");

        var events = await EventsAsync(await PostAsync("t-after", "show me markdown"));

        Assert.DoesNotContain(events, e => e is RunErrorEvent);
        Assert.IsType<RunFinishedEvent>(events[^1]);
    }
}
