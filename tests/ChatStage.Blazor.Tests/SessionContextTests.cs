using System.Runtime.CompilerServices;
using Bit.BlazorUI;

namespace ChatStage.Blazor.Tests;

public sealed class SessionContextTests
{
    [Fact]
    public async Task TransformsPromptAndNotifiesBeforeStreamingCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new GatedTransport();
        await using var session = CreateSession(transport);
        using var context = new ChatStageSessionContext(session) { InputText = "/ask hello" };
        var changes = 0;
        ChatStageSubmission? sent = null;
        context.Changed += () => changes++;
        context.MessageSent += value => sent = value;
        context.Submitting = (value, _) =>
        {
            value.Text = value.Text.Replace("/ask ", string.Empty, StringComparison.Ordinal);
            return Task.CompletedTask;
        };

        var send = context.SubmitAsync(cancellationToken: ct);
        await transport.Started.Task.WaitAsync(ct);
        Assert.True(context.IsBusy);
        Assert.Equal("hello", sent?.Text);
        Assert.Equal("hello", transport.Request?.Messages[^1].GetText());
        Assert.Empty(context.InputText);
        context.InputText = "next";
        Assert.False(await context.SubmitAsync(cancellationToken: ct));
        Assert.Equal("next", context.InputText);
        transport.Release.TrySetResult();
        Assert.True(await send.WaitAsync(ct));
        Assert.False(context.IsBusy);
        Assert.True(changes > 0);
        Assert.Same(context.Actions, ChatStageActions.For(session));
    }

    [Fact]
    public async Task RejectedAndCancelledInterceptionsKeepDraftAndNeverSend()
    {
        var transport = new GatedTransport();
        await using var session = CreateSession(transport);
        using var context = new ChatStageSessionContext(session) { InputText = "keep me" };
        context.Submitting = (value, _) =>
        {
            value.Cancel = true;
            return Task.CompletedTask;
        };
        Assert.False(await context.SubmitAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("keep me", context.InputText);
        Assert.Empty(context.Conversation.Messages);
        context.Submitting = null;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.SubmitAsync(cancellationToken: cancelled.Token));
        Assert.False(context.IsBusy);
        Assert.Equal("keep me", context.InputText);
        Assert.Null(transport.Request);
    }

    [Fact]
    public async Task StopCancelsStreamAndFocusAndEscapeArePublicHooks()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new GatedTransport();
        await using var session = CreateSession(transport);
        using var context = new ChatStageSessionContext(session) { InputText = "hello" };
        var focused = false;
        var escaped = false;
        context.FocusInput = () => { focused = true; return ValueTask.CompletedTask; };
        context.Escape += () => escaped = true;
        await context.FocusInputAsync();
        context.NotifyEscape();
        Assert.True(focused);
        Assert.True(escaped);
        var send = context.SubmitAsync(cancellationToken: ct);
        await transport.Started.Task.WaitAsync(ct);
        context.Stop();
        await send.WaitAsync(ct);
        Assert.False(context.IsBusy);
    }

    [Fact]
    public async Task HydratedConversationKeepsThreadAndContextDisposalDetachesNotifications()
    {
        var conversation = new BitAiConversation("saved-thread");
        var previous = new BitAiMessage(BitAiRole.User);
        previous.AddPart(new BitAiTextPart("previous"));
        conversation.AddMessage(previous);
        var transport = new GatedTransport();
        transport.Release.TrySetResult();
        await using var session = new BitAiChatSession(transport, conversation, new() { ExecuteTools = false, InterruptOnSubmit = false, Pacing = null });
        var context = new ChatStageSessionContext(session);
        Assert.Same(conversation, context.Conversation);
        Assert.True(await context.SubmitAsync("next", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("saved-thread", transport.Request?.ConversationId);
        Assert.Equal("previous", transport.Request?.Messages[0].GetText());
        var changes = 0;
        context.Changed += () => changes++;
        context.Dispose();
        conversation.AddMessage(new(BitAiRole.Assistant));
        Assert.Equal(0, changes);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.SubmitAsync("disposed", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static BitAiChatSession CreateSession(IBitAiTransport transport) =>
        new(transport, new("thread"), new() { ExecuteTools = false, InterruptOnSubmit = false, Pacing = null });

    private sealed class GatedTransport : IBitAiTransport
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BitAiRequest? Request { get; private set; }

        public async IAsyncEnumerable<BitAiEvent> StreamAsync(BitAiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Request = request;
            yield return new BitAiMessageStartedEvent(BitAiRole.Assistant);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return new BitAiTextDeltaEvent("**ok**");
            yield return new BitAiMessageCompletedEvent(BitAiFinishReason.Stop);
        }
    }
}
