using Bit.BlazorUI;
using Microsoft.Extensions.DependencyInjection;

namespace ChatStage.IntegrationTests;

/// <summary>
/// End to end: Bit.BlazorUI.AI's real chat session, through ChatStage's AG-UI transport, against a real Agent Framework
/// server served by ChatStage.Server.
/// </summary>
public sealed class ChatRoundTripTests : IAsyncLifetime
{
    private AgentServer _server = null!;
    private ServiceProvider _services = null!;
    private AsyncServiceScope _scope;
    private BitAiConversation _conversation = null!;
    private BitAiChatSession _session = null!;
    private readonly Queue<BitAiApprovalDecision> _decisions = new();
    private readonly CancellationTokenSource _stop = new();

    public async ValueTask InitializeAsync()
    {
        _server = await AgentServer.StartAsync();
        _services = Services(AgentServer.AccessToken);
        _scope = _services.CreateAsyncScope();
        var factory = _scope.ServiceProvider.GetRequiredService<BitAiChatSessionFactory>();
        _conversation = new BitAiConversation("thread-" + Guid.NewGuid().ToString("N")[..8]);
        _session = factory.Create(_conversation, factory.Defaults);
        _ = AnswerApprovalsAsync(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _session.DisposeAsync();
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
        await _server.DisposeAsync();
        _stop.Dispose();
    }

    private ServiceProvider Services(string? token)
    {
        var services = new ServiceCollection().AddLogging();
        var chat = services.AddChatStage(o =>
        {
            o.Endpoint = new Uri(_server.TestServer.BaseAddress, "agent");
            o.ContextWindow = 8000;
        })
        .AddToolComponent<ListingCard, AgentServer.Listing>("find_listing")
        .AddEventComponent<InvoiceCard, InvoiceCard.Invoice>("demo:invoice");
        if (token is not null)
        {
            chat.AddBearerToken((_, _) => ValueTask.FromResult<string?>(token));
        }

        chat.HttpClient.ConfigurePrimaryHttpMessageHandler(() => _server.TestServer.CreateHandler());
        return services.BuildServiceProvider();
    }

    /// <summary>Answers approval prompts like a user clicking Allow or Decline: a server-held call waits in AwaitingApproval.</summary>
    private async Task AnswerApprovalsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(20, CancellationToken.None);
            var waiting = _conversation.Messages.SelectMany(m => m.Parts.OfType<BitAiToolCallPart>()).FirstOrDefault(p => p.Status == BitAiToolCallStatus.AwaitingApproval);
            if (waiting is not null && _decisions.TryDequeue(out var decision))
            {
                _session.Approve(waiting.ToolCallId, decision);
            }
        }
    }

    private async Task<BitAiMessage> TurnAsync(string text)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var reply = await _session.SubmitAsync(text, null, timeout.Token);
        return Assert.IsType<BitAiMessage>(reply);
    }

    private static BitAiToolCallPart Tool(BitAiMessage message, string name) =>
        Assert.Single(message.Parts.OfType<BitAiToolCallPart>(), p => p.ToolName == name);

    [Fact]
    public async Task MarkdownStreamsWithUsageForTheContextMeter()
    {
        var reply = await TurnAsync("show me markdown");

        Assert.Contains("```csharp", reply.GetText(), StringComparison.Ordinal);
        Assert.True(reply.Metadata?.Usage.InputTokens > 0);
        Assert.Equal(8000, reply.Metadata?.ContextWindow);
        Assert.True(BitAiContextMeter.Measure(_conversation).Used > 0);
    }

    [Fact]
    public async Task AToolCallShowsAsACompletedCardThenText()
    {
        var reply = await TurnAsync("what time is it");

        var call = Tool(reply, "get_time");
        Assert.Equal(BitAiToolCallStatus.Completed, call.Status);
        Assert.Equal("12:00", call.Result?.GetDisplayText());
        Assert.Contains("Done", reply.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApprovedCallRunsOnceAndTheTurnContinues()
    {
        _decisions.Enqueue(BitAiApprovalDecision.Approved);

        var reply = await TurnAsync("delete the demo item");

        var call = Tool(reply, "delete_item");
        Assert.Equal(BitAiApprovalDecision.Approved, call.Approval);
        Assert.Equal(BitAiToolCallStatus.Completed, call.Status);
        Assert.Equal(("delete_item", "demo-42"), Assert.Single(_server.Tools.Calls));
        Assert.Contains("Done", reply.GetText(), StringComparison.Ordinal);

        // The thread carries on normally afterwards.
        var next = await TurnAsync("show me markdown");
        Assert.Contains("Hello", next.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeclinedCallDoesntRunAndStaysDeclined()
    {
        _decisions.Enqueue(BitAiApprovalDecision.Rejected);

        var reply = await TurnAsync("delete the demo item");

        var call = Tool(reply, "delete_item");
        Assert.Equal(BitAiToolCallStatus.Rejected, call.Status);
        Assert.Empty(_server.Tools.Calls);
        Assert.Contains("did not", reply.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoppingWhileAnApprovalIsPendingLeavesNothingRunAndTheThreadUsable()
    {
        var turn = _session.SubmitAsync("delete the demo item", null, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _conversation.Messages.SelectMany(m => m.Parts.OfType<BitAiToolCallPart>()).Any(p => p.Status == BitAiToolCallStatus.AwaitingApproval));

        _session.Stop();
        await turn.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var next = await TurnAsync("show me markdown");

        Assert.Empty(_server.Tools.Calls);
        Assert.Contains("Hello", next.GetText(), StringComparison.Ordinal);
        Assert.Null(next.Error);
    }

    [Fact]
    public async Task AToolResultCarriesTypedDataForItsComponent()
    {
        var reply = await TurnAsync("find a listing");

        var call = Tool(reply, "find_listing");
        Assert.Equal(84.50m, call.Result?.Value?.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task ACustomEventBecomesAPartForItsComponent()
    {
        var reply = await TurnAsync("show the invoice");

        var part = Assert.Single(reply.Parts.OfType<BitAiStructuredDataPart>());
        Assert.Equal("demo:invoice", part.SchemaName);
        Assert.Equal("INV-7", part.Value?.GetProperty("number").GetString());
    }

    [Fact]
    public async Task AComponentsMessageDuringAReplyIsQueuedAndAnswered()
    {
        var actions = ChatStageActions.For(_session);
        var turn = _session.SubmitAsync("find a listing", null, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => _session.IsGenerating);

        var watch = actions.SendAsync("watch lot-1187", TestContext.Current.CancellationToken);
        await turn.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var reply = await watch.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Contains("Watching", reply?.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunThatFailsWithADescribedExceptionShowsItsMessage()
    {
        var reply = await TurnAsync("fail: user-facing");

        var error = reply.Error ?? _session.LastError;
        Assert.Equal(AgentServer.UserFacingMessage, error?.Message);
    }

    [Fact]
    public async Task ARunThatFailsWithAnyOtherExceptionShowsOnlyTheGenericMessage()
    {
        var reply = await TurnAsync("fail: internal");

        var error = Assert.IsType<BitAiError>(reply.Error ?? _session.LastError);
        Assert.Equal(AgentServer.GenericError, error.Message);
        Assert.DoesNotContain("hunter2", error.Message + error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShownComponentIsInTheTranscriptButNeverReachesTheAgent()
    {
        await TurnAsync("show me markdown");
        var actions = ChatStageActions.For(_session);

        var shown = await actions.ShowAsync("demo:invoice", new InvoiceCard.Invoice("INV-9", 3.25m), "Your latest invoice:", TestContext.Current.CancellationToken);
        var next = await TurnAsync("what time is it");

        Assert.NotNull(shown);
        Assert.Equal("INV-9", Assert.Single(shown.Parts.OfType<BitAiStructuredDataPart>()).Value?.GetProperty("number").GetString());
        Assert.Contains("Done", next.GetText(), StringComparison.Ordinal);
        var sentToModel = string.Join("\n", _server.Model.Requests.SelectMany(r => r).Select(m => m.Text));
        Assert.DoesNotContain("INV-9", sentToModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Your latest invoice", sentToModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheTokenTheChatShowsAnAuthenticationError()
    {
        await using var services = Services(token: null);
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<BitAiChatSessionFactory>();
        var conversation = new BitAiConversation("anonymous");
        await using var session = factory.Create(conversation, factory.Defaults);

        var reply = await session.SubmitAsync("hello", null, TestContext.Current.CancellationToken);

        Assert.Equal(BitAiErrorKind.Authentication, (reply?.Error ?? session.LastError)?.Kind);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
