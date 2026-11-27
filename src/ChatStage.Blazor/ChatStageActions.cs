using System.Runtime.CompilerServices;
using Bit.BlazorUI;

namespace ChatStage;

/// <summary>
/// Lets a component inside the chat talk back to the agent. A message sent while the agent is still replying is
/// queued and sent, in order, as soon as the reply ends, so a click is never silently dropped; <see cref="IsBusy"/> and
/// <see cref="QueuedCount"/> let the component show that it is waiting.
/// </summary>
public sealed class ChatStageActions
{
    private static readonly ConditionalWeakTable<BitAiChatSession, ChatStageActions> Instances = [];
    private static readonly ChatStageActions Detached = new(null);

    private readonly BitAiChatSession? _session;
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

        var item = new Queued(text);
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
                BitAiMessage? reply;
                try
                {
                    reply = await _session.SubmitAsync(next.Text, null, CancellationToken.None).ConfigureAwait(false);
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

    private sealed class Queued(string text)
    {
        public string Text { get; } = text;

        public TaskCompletionSource<BitAiMessage?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }
    }
}
