using Bit.BlazorUI;

namespace ChatStage;

/// <summary>A UI-independent controller over the same session used by the default chat.</summary>
/// <remarks>Dispose the context to detach its notifications. The caller owns the supplied session.</remarks>
public sealed class ChatStageSessionContext : IDisposable
{
    private bool _submitting;
    private bool _disposed;
    private CancellationTokenSource? _submissionCancellation;
    private readonly ChatStageOptions? _options;

    /// <summary>Wraps an existing session without taking ownership of it.</summary>
    /// <param name="session">The session to drive.</param>
    /// <param name="options">Optional final-text reconciliation settings.</param>
    public ChatStageSessionContext(BitAiChatSession session, ChatStageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        _options = options;
        Session.Processor.EventApplied += OnEventApplied;
        Session.StateChanged += NotifyChanged;
        Conversation.Changed += OnConversationChanged;
        Conversation.MessageUpdated += OnMessageUpdated;
    }

    /// <summary>The conversation, whose id is the AG-UI thread id. Hydrate it before supplying it.</summary>
    public BitAiConversation Conversation => Session.Conversation;

    /// <summary>The underlying public Bit session, including approval decisions and retry operations.</summary>
    public BitAiChatSession Session { get; }

    /// <summary>Queued send and app-message actions, shared with registered tool components.</summary>
    public ChatStageActions Actions => ChatStageActions.For(Session);

    /// <summary>Whether a reply or submission is in progress.</summary>
    public bool IsBusy => _submitting || Session.IsGenerating;

    /// <summary>The currently unanswered approval requests.</summary>
    public IReadOnlyList<BitAiToolCallPart> PendingApprovals => Conversation.Messages
        .SelectMany(m => m.Parts).OfType<BitAiToolCallPart>()
        .Where(p => p.Status == BitAiToolCallStatus.AwaitingApproval).ToArray();

    /// <summary>The app's composer draft. Custom composers can bind directly to it.</summary>
    public string InputText { get; set; } = string.Empty;

    /// <summary>Optional interception before sending; mutate text/attachments or set Cancel.</summary>
    public Func<ChatStageSubmission, CancellationToken, Task>? Submitting { get; set; }

    /// <summary>A custom composer's focus implementation, called by FocusInputAsync.</summary>
    public Func<ValueTask>? FocusInput { get; set; }

    /// <summary>
    /// Raised when the conversation, the session or the busy state changes, including on every streamed delta, possibly
    /// on a background thread. Use <c>InvokeAsync</c> in a component; <c>ChatStageSession</c> already renders its layout
    /// for you, coalesced.
    /// </summary>
    public event Action? Changed;

    /// <summary>Raised after an accepted submission is added to the conversation, before the reply completes.</summary>
    public event Action<ChatStageSubmission>? MessageSent;

    /// <summary>Raised by a composer's Escape hook; the app can dismiss its command popup.</summary>
    public event Action? Escape;

    /// <summary>Focuses the registered composer without browser queries.</summary>
    public ValueTask FocusInputAsync() => FocusInput?.Invoke() ?? ValueTask.CompletedTask;

    /// <summary>Signals Escape to the app.</summary>
    public void NotifyEscape() => Escape?.Invoke();

    /// <summary>Replaces an assistant message's prose, preserving tool, approval and reasoning parts.</summary>
    /// <param name="text">Authoritative Markdown after the last text delta.</param>
    /// <param name="messageId">The local message id, or null for the current streamed reply.</param>
    public void ReplaceMessageText(string text, string? messageId = null)
    {
        var message = messageId is null ? Session.Processor.Current : Conversation.FindMessage(messageId);
        if (message is null || !message.Role.IsAssistant)
        {
            throw new InvalidOperationException("An assistant message must exist before its text can be replaced.");
        }

        var parts = message.Parts.OfType<BitAiTextPart>().Where(p => p is not BitAiReasoningPart).ToArray();
        if (parts.Length == 0)
        {
            message.AddPart(new BitAiTextPart(text));
        }
        else
        {
            parts[0].SetText(text);
            foreach (var extra in parts.Skip(1))
            {
                message.RemovePart(extra.PartId);
            }
        }
    }

    private void OnEventApplied(BitAiEvent evt)
    {
        if (evt is BitAiCustomEvent custom && _options?.FinalText?.Invoke(custom) is { } text)
        {
            ReplaceMessageText(text);
        }
    }

    /// <summary>Stops the current reply.</summary>
    public void Stop()
    {
        _submissionCancellation?.Cancel();
        Session.Stop();
    }

    /// <summary>Sends the draft, or explicit text and attachments, after optional interception.</summary>
    /// <param name="text">Text to send, or null for InputText.</param>
    /// <param name="attachments">Attachments to submit through the existing transport.</param>
    /// <param name="cancellationToken">Cancels interception and the active run.</param>
    /// <returns>Whether the submission was accepted. Busy or cancelled submissions retain the draft.</returns>
    public async Task<bool> SubmitAsync(string? text = null, IReadOnlyList<BitAiAttachment>? attachments = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBusy)
        {
            return false;
        }

        var draft = InputText;
        var submission = new ChatStageSubmission(text ?? draft, attachments ?? []);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _submissionCancellation = cancellation;
        cancellationToken = cancellation.Token;
        _submitting = true;
        NotifyChanged();
        try
        {
            if (Submitting is not null)
            {
                await Submitting(submission, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (submission.Cancel || (string.IsNullOrWhiteSpace(submission.Text) && submission.Attachments.Count == 0))
            {
                return false;
            }

            // The submission is accepted once the session adds the user message, whenever it does so; a submission the
            // session turns away ends without adding one.
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnAdded(BitAiConversation _, BitAiMessage message)
            {
                if (message.Role.IsUser)
                {
                    accepted.TrySetResult();
                }
            }

            Task<BitAiMessage?> reply;
            Conversation.MessageAdded += OnAdded;
            try
            {
                reply = Session.SubmitAsync(submission.Text, submission.Attachments, cancellationToken);
                await Task.WhenAny(accepted.Task, reply);
            }
            finally
            {
                Conversation.MessageAdded -= OnAdded;
            }

            if (!accepted.Task.IsCompleted)
            {
                await reply;
                return false;
            }

            if (InputText == draft && (text is null || text == draft))
            {
                InputText = string.Empty;
            }

            MessageSent?.Invoke(submission);
            NotifyChanged();
            await reply;
            return true;
        }
        finally
        {
            _submitting = false;
            _submissionCancellation = null;
            NotifyChanged();
        }
    }

    private void OnConversationChanged(BitAiConversation _) => NotifyChanged();
    private void OnMessageUpdated(BitAiConversation conversation, BitAiMessage message) => NotifyChanged();

    private void NotifyChanged() => Changed?.Invoke();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _submissionCancellation?.Cancel();
        Session.StateChanged -= NotifyChanged;
        Session.Processor.EventApplied -= OnEventApplied;
        Conversation.Changed -= OnConversationChanged;
        Conversation.MessageUpdated -= OnMessageUpdated;
        FocusInput = null;
    }
}

/// <summary>A mutable submission passed to the app before the session sends it.</summary>
/// <param name="text">The original prompt.</param>
/// <param name="attachments">The original attachments.</param>
public sealed class ChatStageSubmission(string text, IReadOnlyList<BitAiAttachment> attachments)
{
    /// <summary>The prompt to send. Can be transformed by the app.</summary>
    public string Text { get; set; } = text;

    /// <summary>The attachments to send. Can be replaced after upload or transformation.</summary>
    public IReadOnlyList<BitAiAttachment> Attachments { get; set; } = attachments;

    /// <summary>Set true to keep the draft without sending.</summary>
    public bool Cancel { get; set; }
}
