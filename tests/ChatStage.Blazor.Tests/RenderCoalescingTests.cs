using Bit.BlazorUI;

namespace ChatStage.Blazor.Tests;

public sealed class RenderCoalescingTests
{
    [Fact]
    public async Task TokensAreCoalescedButToolApprovalErrorAndCompletionFlushImmediately()
    {
        await using var session = new BitAiChatSession(new UnusedTransport(), new("render"), new() { Pacing = null });
        using var context = new ChatStageSessionContext(session);
        var renders = 0;
        await using var subscription = new ChatStageRenderSubscription(context, TimeSpan.FromSeconds(30), () =>
        {
            renders++;
            return Task.CompletedTask;
        });
        session.Processor.BeginGeneration();
        session.Processor.Apply(new BitAiMessageStartedEvent(BitAiRole.Assistant));
        var start = renders;
        for (var i = 0; i < 1000; i++)
        {
            session.Processor.Apply(new BitAiTextDeltaEvent("x"));
        }
        Assert.Equal(start, renders);
        session.Processor.Apply(new BitAiToolCallStartedEvent("c", "delete"));
        Assert.True(renders > start);
        var tool = renders;
        session.Processor.Apply(new BitAiToolApprovalRequestedEvent("c"));
        Assert.True(renders > tool);
        var approval = renders;
        session.Processor.Apply(new BitAiErrorEvent(new("error"), isTerminal: true));
        Assert.True(renders > approval);
        var error = renders;
        session.Processor.Apply(new BitAiMessageCompletedEvent(BitAiFinishReason.Stop));
        Assert.True(renders > error);
    }

    private sealed class UnusedTransport : IBitAiTransport
    {
        public IAsyncEnumerable<BitAiEvent> StreamAsync(BitAiRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
