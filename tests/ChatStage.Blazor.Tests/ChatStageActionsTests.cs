using System.Runtime.CompilerServices;
using Bit.BlazorUI;
using Microsoft.Extensions.DependencyInjection;

namespace ChatStage.Blazor.Tests;

/// <summary>A component's messages to the agent: sent when idle, queued (not dropped) while a reply is running.</summary>
public sealed class ChatStageActionsTests
{
    [Fact]
    public async Task AMessageSentDuringAReplyIsQueuedAndSentWhenTheReplyEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new GatedTransport();
        var services = new ServiceCollection().AddLogging();
        services.AddBitBlazorUIAIServices(ai =>
        {
            ai.UseTransport(transport);
            ai.ConfigureSessions(o => o with { ExecuteTools = false, InterruptOnSubmit = false });
        });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<BitAiChatSessionFactory>();
        await using var session = factory.Create(new BitAiConversation("c"), factory.Defaults);
        var actions = ChatStageActions.For(session);

        var first = session.SubmitAsync("first", null, ct);
        await transport.Started.Task.WaitAsync(ct);
        Assert.True(actions.IsBusy);

        var fromCard = actions.SendAsync("watch lot-1", ct);
        Assert.Equal(1, actions.QueuedCount);
        Assert.False(fromCard.IsCompleted);

        transport.Release.TrySetResult();
        await first.WaitAsync(ct);
        var reply = await fromCard.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.NotNull(reply);
        Assert.Equal(0, actions.QueuedCount);
        Assert.Equal(["first", "watch lot-1"], transport.Prompts);
    }

    [Fact]
    public async Task QueuedMessagesKeepTheirOrderAndACancelledOneIsNotSent()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new GatedTransport();
        var services = new ServiceCollection().AddLogging();
        services.AddBitBlazorUIAIServices(ai =>
        {
            ai.UseTransport(transport);
            ai.ConfigureSessions(o => o with { ExecuteTools = false, InterruptOnSubmit = false });
        });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<BitAiChatSessionFactory>();
        await using var session = factory.Create(new BitAiConversation("c"), factory.Defaults);
        var actions = ChatStageActions.For(session);

        var first = session.SubmitAsync("first", null, ct);
        await transport.Started.Task.WaitAsync(ct);
        using var cancelB = new CancellationTokenSource();
        var a = actions.SendAsync("a", ct);
        var b = actions.SendAsync("b", cancelB.Token);
        var c = actions.SendAsync("c", ct);
        await cancelB.CancelAsync();

        transport.Release.TrySetResult();
        await first.WaitAsync(ct);
        await Task.WhenAll(a, c).WaitAsync(TimeSpan.FromSeconds(10), ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        Assert.Equal(["first", "a", "c"], transport.Prompts);
    }

    [Fact]
    public async Task AChatWithoutASessionCantSend()
    {
        var actions = ChatStageActions.For(null);

        Assert.False(actions.CanSend);
        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.SendAsync("x", TestContext.Current.CancellationToken));
    }

    /// <summary>Replies "ok"; the first reply waits until <see cref="Release"/> is completed.</summary>
    private sealed class GatedTransport : IBitAiTransport
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Prompts { get; } = [];

        public async IAsyncEnumerable<BitAiEvent> StreamAsync(BitAiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            lock (Prompts)
            {
                Prompts.Add(request.Messages[^1].GetText());
            }

            yield return new BitAiMessageStartedEvent(BitAiRole.Assistant);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return new BitAiTextDeltaEvent("ok");
            yield return new BitAiMessageCompletedEvent(BitAiFinishReason.Stop);
        }
    }
}
