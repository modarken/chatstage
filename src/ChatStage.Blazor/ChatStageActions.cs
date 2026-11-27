using System.Runtime.CompilerServices;
using System.Text.Json;
using Bit.BlazorUI;

namespace ChatStage;

/// <summary>
/// Lets a component inside the chat, or app code around it, talk back to the agent (<see cref="SendAsync"/>) or show one
/// of its own components in the transcript without the agent (<see cref="ShowAsync{TData}"/>). A message sent while the
/// agent is still replying is queued and sent, in order, as soon as the reply ends, so a click is never silently dropped;
/// <see cref="IsBusy"/> and <see cref="QueuedCount"/> let the component show that it is waiting.
/// </summary>
public sealed class ChatStageActions
{
    private static readonly ConditionalWeakTable<BitAiChatSession, ChatStageActions> Instances = [];
    private static readonly ChatStageActions Detached = new(null);
    private static readonly JsonSerializerOptions DefaultJson = new(JsonSerializerDefaults.Web);

    /// <summary>The <see cref="BitAiMessage.Extensions"/> key that marks a message the app showed.</summary>
    internal const string AppMessageExtension = "chatstage.appMessage";

    private readonly BitAiChatSession? _session;
    private ChatStageComponentRegistry? _components;
    private readonly Lock _gate = new();
    private readonly LinkedList<Queued> _queue = [];
    private bool _pumping;

    private ChatStageActions(BitAiChatSession? session)
    {
        _session = session;
        if (session is not null)
        {
            session.StateChanged += OnSessionChanged;
        }
    }

    /// <summary>Raised when <see cref="IsBusy"/> or <see cref="QueuedCount"/> may have changed. It can be raised on any thread.</summary>
    public event Action? Changed;

    /// <summary>The chat session this talks to, or <see langword="null"/> when the chat is shown without one (read-only).</summary>
    public BitAiChatSession? Session => _session;

    /// <summary>Whether messages can be sent at all: <see langword="false"/> for a read-only chat.</summary>
    public bool CanSend => _session is not null;

    /// <summary>Whether the agent is replying now; a message sent meanwhile waits in the queue.</summary>
    public bool IsBusy => _session?.IsGenerating ?? false;

    /// <summary>How many messages are waiting to be sent.</summary>
    public int QueuedCount
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>The actions for a session: one instance per session, shared by every component in its chat.</summary>
    /// <param name="session">The session, or <see langword="null"/> for a chat shown without one.</param>
    public static ChatStageActions For(BitAiChatSession? session) =>
        session is null ? Detached : Instances.GetValue(session, static s => new ChatStageActions(s));

    /// <summary>
    /// Whether <paramref name="message"/> was shown by the app with <see cref="ShowAsync{TData}"/>. Such a message is
    /// only in the transcript: it is never sent to the agent.
    /// </summary>
    /// <param name="message">A message of the conversation.</param>
    /// <returns><see langword="true"/> for a message the app showed.</returns>
    public static bool IsAppMessage(IBitAiMessage message) =>
        message is BitAiMessage { Extensions: var extensions } && extensions.ContainsKey(AppMessageExtension);

    /// <summary>The actions for a session, using <paramref name="components"/> to check and serialize what <see cref="ShowAsync{TData}"/> shows.</summary>
    internal static ChatStageActions For(BitAiChatSession? session, ChatStageComponentRegistry components)
    {
        var actions = For(session);
        if (actions._session is not null)
        {
            actions._components = components;
        }

        return actions;
    }

    /// <summary>
    /// Shows the component registered for <paramref name="eventName"/> (with
    /// <see cref="ChatStageBuilder.AddEventComponent{TComponent, TData}(string)"/>) in the transcript, with
    /// <paramref name="data"/>, as an assistant message from the app rather than the agent: for example a "View matches"
    /// button that shows matching listings as cards. No run is started, and the message is never sent to the agent, as
    /// history or otherwise.
    /// </summary>
    /// <remarks>
    /// Like <see cref="SendAsync"/>, it waits while the agent is replying and keeps its place among queued messages, so it
    /// never lands inside a reply. <paramref name="data"/> is serialized with <see cref="ChatStageBuilder.ConfigureJson"/>'s
    /// options, as the agent's own event values are read, so the message can be saved with the conversation.
    /// </remarks>
    /// <typeparam name="TData">The component's data type.</typeparam>
    /// <param name="eventName">The name the component is registered under, for example <c>acme:listings</c>.</param>
    /// <param name="data">The component's data.</param>
    /// <param name="text">Text shown above the component, or <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Takes a message that is still queued out of the queue.</param>
    /// <returns>The message once it is in the transcript, or <see langword="null"/> when the session ended first.</returns>
    /// <exception cref="InvalidOperationException">The chat has no session (<see cref="CanSend"/> is false).</exception>
    /// <exception cref="ArgumentException">No component is registered for <paramref name="eventName"/>.</exception>
    public Task<BitAiMessage?> ShowAsync<TData>(string eventName, TData data, string? text = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        if (_session is null)
        {
            throw new InvalidOperationException("This chat is shown without a session, so it can't show messages.");
        }

        var components = _components;
        if (components is not null && !components.HasEvent(eventName))
        {
            throw new ArgumentException($"No component is registered for '{eventName}'. Register one with {nameof(ChatStageBuilder.AddEventComponent)}.", nameof(eventName));
        }

        var value = JsonSerializer.SerializeToElement(data, components?.Json ?? DefaultJson);
        var message = new BitAiMessage(BitAiRole.Assistant);
        message.Extensions[AppMessageExtension] = true;
        if (!string.IsNullOrEmpty(text))
        {
            message.AddPart(new BitAiTextPart(text));
        }

        message.AddPart(new BitAiStructuredDataPart(value, eventName));
        return Enqueue(new Queued(text: null, message), cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="text"/> to the agent as a user message: now when the agent is idle, otherwise as soon as its
    /// current reply ends (messages keep their order).
    /// </summary>
    /// <param name="text">The message.</param>
    /// <param name="cancellationToken">Takes a message that is still queued out of the queue.</param>
    /// <returns>The agent's reply once it has finished, or <see langword="null"/> when the session ended first.</returns>
    /// <exception cref="InvalidOperationException">The chat has no session (<see cref="CanSend"/> is false).</exception>
    public Task<BitAiMessage?> SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (_session is null)
        {
            throw new InvalidOperationException("This chat is shown without a session, so it can't send messages.");
        }

        return Enqueue(new Queued(text, message: null), cancellationToken);
    }

    private Task<BitAiMessage?> Enqueue(Queued item, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _queue.AddLast(item);
        }

        if (cancellationToken.CanBeCanceled)
        {
            item.Registration = cancellationToken.Register(() =>
            {
                bool removed;
                lock (_gate)
                {
                    removed = _queue.Remove(item);
                }

                if (removed)
                {
                    item.Completion.TrySetCanceled(cancellationToken);
                    RaiseChanged();
                }
            });
        }

        RaiseChanged();
        _ = PumpAsync();
        return item.Completion.Task;
    }

    private void OnSessionChanged()
    {
        RaiseChanged();
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        lock (_gate)
        {
            if (_pumping)
            {
                return;
            }

            _pumping = true;
        }

        try
        {
            while (true)
            {
                Queued? next;
                lock (_gate)
                {
                    next = _session!.IsGenerating ? null : _queue.First?.Value;
                    if (next is null)
                    {
                        _pumping = false;
                        return;
                    }

                    _queue.RemoveFirst();
                }

                await next.Registration.DisposeAsync().ConfigureAwait(false);
                RaiseChanged();
                if (next.Message is { } shown)
                {
                    _session.Conversation.AddMessage(shown);
                    next.Completion.TrySetResult(shown);
                    continue;
                }

                BitAiMessage? reply;
                try
                {
                    reply = await _session.SubmitAsync(next.Text!, null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    next.Completion.TrySetResult(null);
                    continue;
                }
                catch (Exception ex)
                {
                    next.Completion.TrySetException(ex);
                    continue;
                }

                if (reply is null && !_session.IsGenerating)
                {
                    // Not sent and nothing running: the session can't take messages any more.
                    next.Completion.TrySetResult(null);
                }
                else if (reply is null)
                {
                    // Another turn started first: keep the message's place and wait for that turn to end.
                    lock (_gate)
                    {
                        _queue.AddFirst(next);
                    }
                }
                else
                {
                    next.Completion.TrySetResult(reply);
                }
            }
        }
        catch
        {
            lock (_gate)
            {
                _pumping = false;
            }

            throw;
        }
    }

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>A queued message: text to send to the agent, or a message the app shows.</summary>
    private sealed class Queued(string? text, BitAiMessage? message)
    {
        public string? Text { get; } = text;

        public BitAiMessage? Message { get; } = message;

        public TaskCompletionSource<BitAiMessage?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }
    }
}
