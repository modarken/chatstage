using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatStage.Server;

/// <summary>
/// Wraps an agent so that tool approvals are bound to the requests the thread recorded.
/// </summary>
/// <remarks>
/// <para>
/// A client answers an approval with the request's id and a yes or no. Whatever tool call the client sends with the
/// answer, the call that runs is the one the agent asked about, with its recorded arguments. An answer to anything the
/// thread never asked (another thread's id, an invented id, one already answered) is dropped, and approval requests in a
/// caller's input are ignored, since only the agent asks. A tampered resume therefore can't run a tool, or change what
/// one does.
/// </para>
/// <para>
/// When a run brings a new message while approvals are still unanswered (the user stopped the turn, or the app
/// restarted), those approvals are declined first, so the thread stays usable and nothing runs without a yes.
/// </para>
/// <para>
/// The binding reads the thread's recorded history from its <see cref="AgentSession"/>, so the agent must be served with
/// a session store (see <see cref="ChatStageServerExtensions.MapChatStageAgent"/>), and the Agent Framework's own approval
/// response binding should stay on (the default).
/// </para>
/// </remarks>
public sealed partial class ApprovalBindingAgent : DelegatingAIAgent
{
    /// <summary>The reason given to the agent for an approval declined because the user moved on without answering.</summary>
    public const string UnansweredReason = "The user sent a new message without answering this approval, so it was declined.";

    private readonly ILogger _logger;

    /// <summary>Wraps <paramref name="innerAgent"/>.</summary>
    /// <param name="innerAgent">The agent whose tool approvals are bound.</param>
    /// <param name="logger">Logs dropped answers and failed runs.</param>
    public ApprovalBindingAgent(AIAgent innerAgent, ILogger<ApprovalBindingAgent>? logger = null)
        : base(innerAgent)
    {
        _logger = logger ?? NullLogger<ApprovalBindingAgent>.Instance;
    }

    /// <inheritdoc/>
    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) =>
        base.RunCoreAsync(Bind(messages, session), session, options, cancellationToken);

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var input = Bind(messages, session);
        var updates = base.RunCoreStreamingAsync(input, session, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using (updates.ConfigureAwait(false))
        {
            while (true)
            {
                try
                {
                    if (!await updates.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && LogRunFailed(ex))
                {
                    throw; // not reached: LogRunFailed returns false, so the exception propagates unchanged
                }

                yield return updates.Current;
            }
        }
    }

    /// <summary>Binds the approval answers in <paramref name="messages"/> to the requests recorded in <paramref name="session"/>.</summary>
    /// <param name="messages">A run's input.</param>
    /// <param name="session">The thread.</param>
    /// <returns>The input to run with: answers bound to their recorded calls first, then the other new messages.</returns>
    internal List<ChatMessage> Bind(IEnumerable<ChatMessage> messages, AgentSession? session)
    {
        var input = messages.ToList();
        var history = session is null ? null : History(session);
        var pending = Pending(history ?? []);
        var bound = new List<AIContent>();
        var rest = new List<ChatMessage>();

        foreach (var message in input)
        {
            var kept = new List<AIContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case ToolApprovalRequestContent:
                        // Only the agent asks; a request in a caller's input is never trusted.
                        break;
                    case ToolApprovalResponseContent answer when pending.Remove(answer.RequestId, out var request):
                        bound.Add(new ToolApprovalResponseContent(answer.RequestId, answer.Approved, request.ToolCall) { Reason = answer.Reason });
                        break;
                    case ToolApprovalResponseContent answer:
                        LogDroppedAnswer(answer.RequestId, history is null);
                        break;
                    default:
                        kept.Add(content);
                        break;
                }
            }

            if (kept.Count == message.Contents.Count)
            {
                rest.Add(message);
            }
            else if (kept.Count > 0)
            {
                var copy = message.Clone();
                copy.Contents = kept;
                rest.Add(copy);
            }
        }

        // A new message while approvals are unanswered: decline them first, so the thread stays consistent.
        if (pending.Count > 0 && rest.Any(m => m.Role == ChatRole.User))
        {
            foreach (var request in pending.Values)
            {
                bound.Add(request.CreateResponse(approved: false, reason: UnansweredReason));
            }
        }

        if (bound.Count == 0)
        {
            return rest;
        }

        return [new ChatMessage(ChatRole.User, bound), .. rest];
    }

    /// <summary>The thread's recorded messages, or <see langword="null"/> when they can't be read.</summary>
    private List<ChatMessage>? History(AgentSession session)
    {
        if (InnerAgent.GetService<InMemoryChatHistoryProvider>() is { } provider)
        {
            return provider.GetMessages(session);
        }

        return session.TryGetInMemoryChatHistory(out var messages) ? messages : null;
    }

    /// <summary>The approval requests in <paramref name="history"/> that have no answer and no result yet, by request id.</summary>
    private static Dictionary<string, ToolApprovalRequestContent> Pending(List<ChatMessage> history)
    {
        var pending = new Dictionary<string, ToolApprovalRequestContent>(StringComparer.Ordinal);
        var settledCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var content in history.SelectMany(m => m.Contents))
        {
            switch (content)
            {
                case ToolApprovalRequestContent request:
                    pending[request.RequestId] = request;
                    break;
                case ToolApprovalResponseContent answer:
                    pending.Remove(answer.RequestId);
                    break;
                case FunctionResultContent result:
                    settledCalls.Add(result.CallId);
                    break;
            }
        }

        foreach (var (id, request) in pending.ToList())
        {
            if (request.ToolCall is FunctionCallContent call && settledCalls.Contains(call.CallId))
            {
                pending.Remove(id);
            }
        }

        return pending;
    }

    private bool LogRunFailed(Exception exception)
    {
        LogRunFailedCore(_logger, Name ?? Id, exception);
        return false;
    }

    private void LogDroppedAnswer(string requestId, bool historyUnreadable)
    {
        if (historyUnreadable)
        {
            LogHistoryUnreadable(_logger, requestId);
        }
        else
        {
            LogUnsolicitedAnswer(_logger, requestId);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent {Agent} run failed")]
    private static partial void LogRunFailedCore(ILogger logger, string agent, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped an approval answer for {RequestId}: this thread has no such unanswered request")]
    private static partial void LogUnsolicitedAnswer(ILogger logger, string requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped an approval answer for {RequestId}: the thread's history can't be read, so it can't be checked (serve the agent with a session store and in-memory chat history)")]
    private static partial void LogHistoryUnreadable(ILogger logger, string requestId);
}
