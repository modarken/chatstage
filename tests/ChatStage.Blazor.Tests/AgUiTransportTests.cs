using System.Net;
using System.Text.Json;
using Bit.BlazorUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatStage.Blazor.Tests;

/// <summary>The AG-UI to Bit mapping, driven through the transport against scripted server-sent event streams.</summary>
public sealed class AgUiTransportTests
{
    private static AgUiTransport Transport(ScriptedHandler handler, Action<ChatStageOptions>? configure = null, ChatStageComponentRegistry? components = null)
    {
        var options = new ChatStageOptions { Endpoint = new Uri("http://agent.test/agent"), ContextWindow = 8000, ModelId = "test-model", ResumeRetryDelay = TimeSpan.Zero };
        configure?.Invoke(options);
        return new AgUiTransport(new HttpClient(handler), Options.Create(options), components ?? new ChatStageComponentRegistry(), NullLogger<AgUiTransport>.Instance);
    }

    private static BitAiRequest Request(string conversationId = "t", params BitAiMessage[] history)
    {
        var messages = history.Length > 0 ? history : [User("hello")];
        return new BitAiRequest(messages) { ConversationId = conversationId };
    }

    private static BitAiMessage User(string text)
    {
        var message = new BitAiMessage(BitAiRole.User, "u-" + Guid.NewGuid().ToString("N")[..6]);
        message.AddPart(new BitAiTextPart(text));
        return message;
    }

    private static BitAiMessage Assistant(string text)
    {
        var message = new BitAiMessage(BitAiRole.Assistant, "a-" + Guid.NewGuid().ToString("N")[..6]);
        message.AddPart(new BitAiTextPart(text));
        return message;
    }

    /// <summary>Runs a turn to the end, answering each approval prompt with the next decision.</summary>
    private static async Task<List<BitAiEvent>> RunAsync(AgUiTransport transport, BitAiRequest request, params BitAiApprovalDecision[] decisions)
    {
        var events = new List<BitAiEvent>();
        var answers = new Queue<BitAiApprovalDecision>(decisions);
        await foreach (var e in transport.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            events.Add(e);
            if (e is BitAiToolApprovalRequestedEvent asked)
            {
                await transport.SendApprovalAsync(new BitAiApprovalResponse(asked.ToolCallId, "tool", answers.Dequeue()), TestContext.Current.CancellationToken);
            }
        }

        return events;
    }

    [Fact]
    public async Task TextStreamsAsDeltasBetweenStartAndCompletion()
    {
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.TextStart(), Sse.Text("Hel"), Sse.Text("lo"), Sse.TextEnd(), Sse.RunFinished()));

        var events = await RunAsync(Transport(handler), Request());

        Assert.IsType<BitAiMessageStartedEvent>(events[0]);
        var metadata = Assert.IsType<BitAiMetadataEvent>(events[1]);
        Assert.Equal(8000, metadata.Metadata.ContextWindow);
        Assert.Equal("test-model", metadata.Metadata.ModelId);
        Assert.Equal(["Hel", "lo"], events.OfType<BitAiTextDeltaEvent>().Select(t => t.Delta));
        Assert.Equal(BitAiFinishReason.Stop, Assert.IsType<BitAiMessageCompletedEvent>(events[^1]).FinishReason);
    }

    [Fact]
    public async Task ToolCallsBecomeToolCardsWithStructuredResults()
    {
        var handler = new ScriptedHandler().Reply(Sse.Stream(
            Sse.RunStarted(), Sse.ToolStart("c1", "find_listing"), Sse.ToolArgs("c1", "{\"query\":"), Sse.ToolArgs("c1", "\"camera\"}"),
            Sse.ToolEnd("c1"), Sse.ToolResult("c1", "{\"id\":\"lot-1\",\"price\":84.5}"), Sse.RunFinished()));

        var events = await RunAsync(Transport(handler), Request());

        Assert.Equal("find_listing", Assert.Single(events.OfType<BitAiToolCallStartedEvent>()).ToolName);
        Assert.Equal(2, events.OfType<BitAiToolCallDeltaEvent>().Count());
        var completed = Assert.Single(events.OfType<BitAiToolCallCompletedEvent>());
        Assert.Equal("camera", completed.Arguments!.Value.GetProperty("query").GetString());
        var result = Assert.Single(events.OfType<BitAiToolResultEvent>());
        Assert.Equal(84.5m, result.Outcome.Value!.Value.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task UsageIsReportedFromRunFinished()
    {
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.Text("hi"), Sse.Usage(120, 30)));

        var events = await RunAsync(Transport(handler), Request());

        var usage = Assert.Single(events.OfType<BitAiUsageUpdatedEvent>()).Usage;
        Assert.Equal(120, usage.InputTokens);
        Assert.Equal(30, usage.OutputTokens);
    }

    [Fact]
    public async Task ARunErrorEndsTheTurnWithATerminalError()
    {
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.Text("par"), Sse.Error("boom", "StreamingError")));

        var events = await RunAsync(Transport(handler), Request());

        var error = Assert.IsType<BitAiErrorEvent>(events[^1]);
        Assert.True(error.IsTerminal);
        Assert.Equal("StreamingError", error.Error.Code);
        Assert.Equal("boom", error.Error.Message); // RUN_ERROR's message is what the user sees
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ARunErrorWithoutAMessageShowsAGenericOne(string message)
    {
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.Error(message)));

        var events = await RunAsync(Transport(handler), Request());

        Assert.Equal("The agent run failed.", Assert.IsType<BitAiErrorEvent>(events[^1]).Error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, BitAiErrorKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, BitAiErrorKind.Authorization)]
    [InlineData(HttpStatusCode.TooManyRequests, BitAiErrorKind.RateLimit)]
    [InlineData(HttpStatusCode.BadGateway, BitAiErrorKind.Provider)]
    public async Task HttpErrorsBecomeTypedErrors(HttpStatusCode status, BitAiErrorKind kind)
    {
        var handler = new ScriptedHandler().Status(status);

        var events = await RunAsync(Transport(handler), Request());

        Assert.Equal(kind, Assert.IsType<BitAiErrorEvent>(events[^1]).Error.Kind);
    }

    [Fact]
    public async Task ARegisteredCustomEventBecomesAPartAndOthersPassThrough()
    {
        var components = new ChatStageComponentRegistry();
        components.AddEvent("acme:invoice", typeof(object), typeof(JsonElement));
        var handler = new ScriptedHandler().Reply(Sse.Stream(
            Sse.RunStarted(), Sse.Custom("acme:invoice", new { total = 12.5 }), Sse.Custom("acme:telemetry", new { ms = 3 }), Sse.RunFinished()));

        var events = await RunAsync(Transport(handler, components: components), Request());

        var part = Assert.Single(events.OfType<BitAiStructuredDataEvent>());
        Assert.Equal("acme:invoice", part.SchemaName);
        Assert.Equal(12.5, part.Value.GetProperty("total").GetDouble());
        Assert.Equal("acme:telemetry", Assert.Single(events.OfType<BitAiCustomEvent>()).Name);
    }

    [Fact]
    public async Task AnApprovalResumesWithOnlyTheIdTheDecisionAndTheCallId()
    {
        var handler = new ScriptedHandler()
            .Reply(Sse.Stream(Sse.RunStarted(), Sse.ToolStart("c1", "delete_item"), Sse.ToolArgs("c1", "{\"itemId\":\"42\"}"), Sse.ToolEnd("c1"), Sse.Interrupt("ficc_c1", "c1", "Delete item 42?")))
            .Reply(Sse.Stream(
                Sse.RunStarted(run: "r2"),
                Sse.ToolStart("c1", "delete_item"), Sse.ToolArgs("c1", "{\"itemId\":\"42\"}"), Sse.ToolEnd("c1"), // replayed by the server
                Sse.ToolResult("c1", "\"deleted 42\""), Sse.Text("Done."), Sse.RunFinished(run: "r2")));

        var events = await RunAsync(Transport(handler), Request(), BitAiApprovalDecision.Approved);

        var asked = Assert.Single(events.OfType<BitAiToolApprovalRequestedEvent>());
        Assert.Equal("c1", asked.ToolCallId);
        Assert.Equal("Delete item 42?", asked.Reason);
        Assert.Single(events.OfType<BitAiToolCallStartedEvent>()); // the replay is not shown twice
        Assert.Single(events.OfType<BitAiToolResultEvent>());

        var resumeRun = handler.Requests[1];
        Assert.Equal(0, resumeRun.GetProperty("messages").GetArrayLength());
        var resume = Assert.Single(resumeRun.GetProperty("resume").EnumerateArray());
        Assert.Equal("ficc_c1", resume.GetProperty("interruptId").GetString());
        Assert.Equal("resolved", resume.GetProperty("status").GetString());
        var payload = resume.GetProperty("payload");
        Assert.True(payload.GetProperty("approved").GetBoolean());
        var toolCall = payload.GetProperty("toolCall");
        Assert.Equal("c1", toolCall.GetProperty("callId").GetString());
        Assert.False(toolCall.TryGetProperty("name", out _));      // the server runs what it recorded,
        Assert.False(toolCall.TryGetProperty("arguments", out _)); // not what the client says
    }

    [Fact]
    public async Task ADeclinedCallKeepsItsRejectedStateInsteadOfTheServersRejectionResult()
    {
        var handler = new ScriptedHandler()
            .Reply(Sse.Stream(Sse.RunStarted(), Sse.ToolStart("c1", "delete_item"), Sse.ToolEnd("c1"), Sse.Interrupt("ficc_c1", "c1")))
            .Reply(Sse.Stream(Sse.RunStarted(), Sse.ToolResult("c1", "Tool call rejected by the approver."), Sse.Text("Not deleted."), Sse.RunFinished()));

        var events = await RunAsync(Transport(handler), Request(), BitAiApprovalDecision.Rejected);

        Assert.Empty(events.OfType<BitAiToolResultEvent>());
        Assert.False(handler.Requests[1].GetProperty("resume")[0].GetProperty("payload").GetProperty("approved").GetBoolean());
        Assert.Contains(events.OfType<BitAiTextDeltaEvent>(), t => t.Delta == "Not deleted.");
    }

    [Fact]
    public async Task AServerThatKeepsHistoryIsSentOnlyTheNewMessage()
    {
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.RunFinished()));

        await RunAsync(Transport(handler), Request("t", User("first"), Assistant("reply"), User("second")));

        var message = Assert.Single(handler.Requests[0].GetProperty("messages").EnumerateArray());
        Assert.Equal("second", message.GetProperty("content").GetString());
    }

    [Fact]
    public async Task AStatelessServerIsSentTheWholeTranscript()
    {
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.RunFinished()));

        await RunAsync(Transport(handler, o => o.ServerKeepsHistory = false), Request("t", User("first"), Assistant("reply"), User("second")));

        Assert.Equal(["user", "assistant", "user"], handler.Requests[0].GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()));
    }

    [Fact]
    public async Task StoppingWhileAnApprovalIsPendingDeclinesItOnTheThreadsNextRun()
    {
        var handler = new ScriptedHandler()
            .Reply(Sse.Stream(Sse.RunStarted(), Sse.ToolStart("c1", "delete_item"), Sse.ToolEnd("c1"), Sse.Interrupt("ficc_c1", "c1")))
            .Reply(Sse.Stream(Sse.RunStarted(), Sse.Text("OK."), Sse.RunFinished()));
        var transport = Transport(handler);

        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var e in transport.StreamAsync(Request(), stop.Token))
                {
                    if (e is BitAiToolApprovalRequestedEvent)
                    {
                        await stop.CancelAsync(); // the user presses Stop while the prompt is showing
                    }
                }
            });
        }

        await RunAsync(transport, Request("t", User("never mind, something else")));

        var next = handler.Requests[1];
        Assert.Equal("never mind, something else", Assert.Single(next.GetProperty("messages").EnumerateArray()).GetProperty("content").GetString());
        var resume = Assert.Single(next.GetProperty("resume").EnumerateArray());
        Assert.Equal("ficc_c1", resume.GetProperty("interruptId").GetString());
        Assert.False(resume.GetProperty("payload").GetProperty("approved").GetBoolean());
    }

    [Fact]
    public async Task AResumeThatFailsToConnectIsRetried()
    {
        var handler = new ScriptedHandler()
            .Reply(Sse.Stream(Sse.RunStarted(), Sse.ToolStart("c1", "delete_item"), Sse.ToolEnd("c1"), Sse.Interrupt("ficc_c1", "c1")))
            .Throw()
            .Reply(Sse.Stream(Sse.RunStarted(), Sse.ToolResult("c1", "\"ok\""), Sse.RunFinished()));

        var events = await RunAsync(Transport(handler), Request(), BitAiApprovalDecision.Approved);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(handler.Requests[1].GetProperty("resume").GetRawText(), handler.Requests[2].GetProperty("resume").GetRawText());
        Assert.Single(events.OfType<BitAiToolResultEvent>());
    }

    [Fact]
    public async Task AFirstRunThatFailsToConnectIsNotRetried()
    {
        var handler = new ScriptedHandler().Throw();

        var events = await RunAsync(Transport(handler), Request());

        Assert.Single(handler.Requests);
        Assert.Equal(BitAiErrorKind.Network, Assert.IsType<BitAiErrorEvent>(events[^1]).Error.Kind);
    }

    [Fact]
    public async Task TheBearerTokenProviderAuthenticatesEveryRun()
    {
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.RunFinished()));
        var services = new ServiceCollection().AddLogging();
        services.AddChatStage(o => o.Endpoint = new Uri("http://agent.test/agent"))
            .AddBearerToken((_, _) => ValueTask.FromResult<string?>("token-1"))
            .AddHeaders((_, _) => ValueTask.FromResult<IEnumerable<KeyValuePair<string, string>>>([new("X-Api-Key", "k")]))
            .HttpClient.ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var transport = scope.ServiceProvider.GetRequiredService<IBitAiTransport>();
        await foreach (var _ in transport.StreamAsync(Request(), TestContext.Current.CancellationToken))
        {
        }

        var sent = Assert.Single(handler.RequestMessages);
        Assert.Equal("Bearer token-1", sent.Headers.Authorization?.ToString());
        Assert.Equal("k", Assert.Single(sent.Headers.GetValues("X-Api-Key")));
    }
}
