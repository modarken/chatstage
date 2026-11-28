using System.Text.Json;
using Bit.BlazorUI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatStage.Blazor.Tests;

public sealed class ConsumerRegressionTests
{
    [Fact]
    public async Task TerminalRunErrorLeavesNeitherSessionNorConversationGenerating()
    {
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.Text("partial"), Sse.Error("failed")));
        await using var session = CreateSession(handler);
        var reply = await session.SubmitAsync("hello", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(session.IsGenerating);
        Assert.False(session.Conversation.IsGenerating);
        Assert.Equal(BitAiMessageStatus.Failed, reply?.Status);
        Assert.Equal("failed", session.LastError?.Message);
    }

    [Fact]
    public async Task FinalCustomTextReplacesStreamedProseAndPreservesToolCards()
    {
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.Text("unresolved reference"),
            Sse.ToolStart("c", "find"), Sse.ToolArgs("c", "{}"), Sse.ToolEnd("c"), Sse.ToolResult("c", "ok"),
            Sse.Custom("app:final", new { text = "Final ![image](/media/1.png)" }), Sse.RunFinished()));
        await using var session = CreateSession(handler);
        using var context = new ChatStageSessionContext(session, new()
        {
            FinalText = e => e.Name == "app:final" && e.Data is JsonElement data ? data.GetProperty("text").GetString() : null
        });
        await context.SubmitAsync("hello", cancellationToken: TestContext.Current.CancellationToken);
        var reply = session.Conversation.LastMessage!;
        Assert.Equal("Final ![image](/media/1.png)", reply.GetText());
        Assert.Single(reply.Parts.OfType<BitAiToolCallPart>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoredApprovalWaitsLocallyThenResumesOnlyTheRecordedIds(bool approve)
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.Text("resumed"), Sse.RunFinished()));
        var transport = CreateTransport(handler);
        await using var session = new BitAiChatSession(transport, new("saved-thread"), new() { ExecuteTools = false, InterruptOnSubmit = false, Pacing = null });
        session.Conversation.AddMessage(BitAiRole.User, "saved history");
        using var context = new ChatStageSessionContext(session);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Processor.EventApplied += evt => { if (evt is BitAiToolApprovalRequestedEvent) waiting.TrySetResult(); };
        var restore = transport.RestoreApprovalsAsync(session,
            [new("interrupt-1", "call-1", "delete", JsonElement.Parse("{\"id\":42}"), "Really delete?")], ct);
        await waiting.Task.WaitAsync(ct);
        Assert.Empty(handler.Requests);
        var card = Assert.Single(context.PendingApprovals);
        Assert.Equal("delete", card.ToolName);
        Assert.Equal(42, card.Arguments?.GetProperty("id").GetInt32());
        Assert.True(session.Approve("call-1", approve ? BitAiApprovalDecision.Approved : BitAiApprovalDecision.Rejected));
        await restore.WaitAsync(ct);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("saved-thread", request.GetProperty("threadId").GetString());
        Assert.Empty(request.GetProperty("messages").EnumerateArray());
        var resume = Assert.Single(request.GetProperty("resume").EnumerateArray());
        Assert.Equal("interrupt-1", resume.GetProperty("interruptId").GetString());
        Assert.DoesNotContain("42", resume.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("saved history", session.Conversation.Messages[0].GetText());
        Assert.False(session.Conversation.IsGenerating);
    }

    private static AgUiTransport CreateTransport(ScriptedHandler handler) => new(new HttpClient(handler),
        Options.Create(new ChatStageOptions { Endpoint = new Uri("https://agent.test/agent") }), NullLogger<AgUiTransport>.Instance);

    private static BitAiChatSession CreateSession(ScriptedHandler handler) => new(CreateTransport(handler), new("t"),
        new() { ExecuteTools = false, InterruptOnSubmit = false, Pacing = null });
}
