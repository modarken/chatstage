using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AGUI.Abstractions;
using AGUI.Client;
using Bit.BlazorUI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatStage;

/// <summary>
/// Bit.BlazorUI.AI's transport contract implemented over AG-UI (HTTP POST + server-sent events).
/// </summary>
/// <remarks>
/// <para>
/// One Bit turn (one call to <see cref="StreamAsync"/>) can span several AG-UI runs. When a run finishes with an
/// <c>interrupt</c> outcome for tool approvals, the transport shows Bit's approval prompt, keeps the turn open, waits
/// for the user's decisions (they arrive through <see cref="SendApprovalAsync"/>), then starts the next run with
/// <c>resume</c> and keeps streaming into the same assistant message.
/// </para>
/// <para>
/// A resume carries only the approval's id, the decision and the tool call's id. The server must run the call it
/// recorded, not anything the client sends: <c>ChatStage.Server</c> does this.
/// </para>
/// </remarks>
public sealed partial class AgUiTransport : IBitAiTransport, IBitAiApprovalTransport
{
    private readonly HttpClient _http;
    private readonly ChatStageOptions _options;
    private readonly ChatStageComponentRegistry _components;
    private readonly ILogger<AgUiTransport> _logger;

    // Decisions the user has yet to make, keyed by tool call id. Completed by SendApprovalAsync.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _waiting = new(StringComparer.Ordinal);

    // Interrupts a cancelled turn left unanswered, per thread: the next run declines them so the thread stays usable.
    private readonly ConcurrentDictionary<string, List<PendingApproval>> _abandoned = new(StringComparer.Ordinal);

    /// <summary>Creates the transport. Normally resolved from dependency injection; see <c>AddChatStage</c>.</summary>
    /// <param name="http">The HTTP client to post runs with (its handlers add any authentication).</param>
    /// <param name="options">The chat options; <see cref="ChatStageOptions.Endpoint"/> is required.</param>
    /// <param name="logger">The logger.</param>
    public AgUiTransport(HttpClient http, IOptions<ChatStageOptions> options, ILogger<AgUiTransport> logger)
        : this(http, options, new ChatStageComponentRegistry(), logger)
    {
    }

    internal AgUiTransport(HttpClient http, IOptions<ChatStageOptions> options, ChatStageComponentRegistry components, ILogger<AgUiTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options.Value;
        _components = components;
        _logger = logger;
        if (_options.Endpoint is null)
        {
            throw new InvalidOperationException($"{nameof(ChatStageOptions)}.{nameof(ChatStageOptions.Endpoint)} must be set to the agent's AG-UI endpoint.");
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<BitAiEvent> StreamAsync(BitAiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var threadId = request.ConversationId ?? Guid.NewGuid().ToString("N");
        var turn = new Turn();
        List<AGUIMessage> messages = _options.ServerKeepsHistory
            ? AgUiMapping.ToAgUiMessages(AgUiMapping.NewMessages(request.Messages))
            : AgUiMapping.ToAgUiMessages(request.Messages);
        IList<AGUIResume>? resume = _abandoned.TryRemove(threadId, out var abandoned)
            ? [.. abandoned.Select(a => AgUiMapping.Resume(a, approved: false))]
            : null;

        yield return new BitAiMessageStartedEvent(BitAiRole.Assistant);
        yield return new BitAiMetadataEvent(new BitAiGenerationMetadata { ModelId = _options.ModelId, ContextWindow = _options.ContextWindow });

        while (true)
        {
            var input = new RunAgentInput
            {
                ThreadId = threadId,
                RunId = Guid.NewGuid().ToString("N"),
                Messages = messages,
                Tools = [],
                Context = [],
                Resume = resume,
            };
            var isResume = resume is { Count: > 0 } && messages.Count == 0;
            RunFinishedEvent? finished = null;
            BitAiError? failure = null;

            await using (var events = RunAsync(input, isResume, cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (true)
                {
                    BaseEvent current;
                    try
                    {
                        if (!await events.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        current = events.Current;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or AgUiHttpException or JsonException or IOException && !cancellationToken.IsCancellationRequested)
                    {
                        failure = AgUiMapping.ToError(ex);
                        LogRunFailed(_logger, threadId, ex);
                        break;
                    }

                    if (current is RunFinishedEvent f)
                    {
                        finished = f;
                        continue;
                    }

                    if (current is RunErrorEvent error)
                    {
                        failure = new BitAiError(error.Message ?? "The agent run failed.") { Code = error.Code, Kind = BitAiErrorKind.Provider };
                        break;
                    }

                    foreach (var mapped in turn.Map(current, _components))
                    {
                        yield return mapped;
                    }
                }
            }

            if (failure is not null)
            {
                yield return new BitAiErrorEvent(failure, isTerminal: true);
                yield break;
            }

            if (finished?.Usage is { Count: > 0 } usage)
            {
                yield return new BitAiUsageUpdatedEvent(turn.AddUsage(usage[^1]));
            }

            if (finished?.Outcome is not RunFinishedInterruptOutcome interrupt)
            {
                break;
            }

            // Human in the loop: each tool-approval interrupt becomes Bit's approval prompt.
            var approvals = interrupt.Interrupts
                .Where(i => i.ToolCallId is not null && i.Id is not null)
                .Select(i => new PendingApproval(i.Id!, i.ToolCallId!, i.Message))
                .ToList();
            if (approvals.Count == 0 || approvals.Count != interrupt.Interrupts.Count)
            {
                yield return new BitAiErrorEvent(new BitAiError("The agent asked for input this chat can't give.") { Kind = BitAiErrorKind.InvalidRequest }, isTerminal: true);
                yield break;
            }

            foreach (var approval in approvals)
            {
                _waiting[approval.ToolCallId] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                yield return new BitAiToolApprovalRequestedEvent(approval.ToolCallId) { Reason = approval.Message, AllowApproveAlways = false };
            }

            var answers = new List<AGUIResume>(approvals.Count);
            try
            {
                foreach (var approval in approvals)
                {
                    var approved = await _waiting[approval.ToolCallId].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    _waiting.TryRemove(approval.ToolCallId, out _);
                    if (!approved)
                    {
                        turn.Declined.Add(approval.ToolCallId);
                    }

                    answers.Add(AgUiMapping.Resume(approval, approved));
                }
            }
            finally
            {
                if (answers.Count < approvals.Count)
                {
                    // Stopped while waiting: decline what's left on the thread's next run, so the thread stays usable.
                    foreach (var approval in approvals)
                    {
                        _waiting.TryRemove(approval.ToolCallId, out _);
                    }

                    _abandoned[threadId] = [.. approvals.Skip(answers.Count)];
                }
            }

            messages = [];
            resume = answers;
        }

        yield return new BitAiMessageCompletedEvent(BitAiFinishReason.Stop);
    }

    /// <inheritdoc/>
    public ValueTask SendApprovalAsync(BitAiApprovalResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (_waiting.TryGetValue(response.ToolCallId, out var decision))
        {
            decision.TrySetResult(response.Decision is BitAiApprovalDecision.Approved or BitAiApprovalDecision.ApprovedAlways);
        }
        else
        {
            // No turn is waiting for it (the turn was stopped, or the app restarted): the server declines it on the next run.
            LogNoPendingApproval(_logger, response.ToolCallId);
        }

        return ValueTask.CompletedTask;
    }

    private async IAsyncEnumerable<BaseEvent> RunAsync(RunAgentInput input, bool retryable, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = JsonContent.Create(input, options: AgUiMapping.Wire),
            };
            request.Headers.Accept.ParseAdd("text/event-stream");
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (HttpRequestException ex) when (retryable && attempt < _options.ResumeRetries && !cancellationToken.IsCancellationRequested)
            {
                // The resume didn't reach the server (or its answer was lost): try again. A ChatStage server ignores an
                // answer it already has, so a repeat can't run a tool twice.
                LogResumeRetry(_logger, input.ThreadId, attempt + 1, ex);
                await Task.Delay(_options.ResumeRetryDelay * Math.Pow(2, attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new AgUiHttpException(response.StatusCode, response.ReasonPhrase);
            }

            await foreach (var e in response.ReadAGUIEventStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return e;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AG-UI run on thread {ThreadId} failed")]
    private static partial void LogRunFailed(ILogger logger, string threadId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Approval resume on thread {ThreadId} failed to connect; retry {Attempt}")]
    private static partial void LogResumeRetry(ILogger logger, string? threadId, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "No turn is waiting for the approval of tool call {ToolCallId}; it will be declined on the thread's next run")]
    private static partial void LogNoPendingApproval(ILogger logger, string toolCallId);

    /// <summary>The state of one Bit turn across its AG-UI runs.</summary>
    internal sealed class Turn
    {
        private readonly Dictionary<string, ToolCall> _calls = new(StringComparer.Ordinal);
        private BitAiTokenUsage _usage;

        /// <summary>Tool calls the user declined: the server's "rejected" result for them is not shown, Bit keeps its Rejected state.</summary>
        public HashSet<string> Declined { get; } = new(StringComparer.Ordinal);

        public IEnumerable<BitAiEvent> Map(BaseEvent e, ChatStageComponentRegistry components)
        {
            switch (e)
            {
                case TextMessageContentEvent { Delta.Length: > 0 } t:
                    yield return new BitAiTextDeltaEvent(t.Delta);
                    break;
                case TextMessageChunkEvent { Delta.Length: > 0 } t:
                    yield return new BitAiTextDeltaEvent(t.Delta);
                    break;
                case ReasoningMessageContentEvent { Delta.Length: > 0 } r:
                    yield return new BitAiReasoningDeltaEvent(r.Delta);
                    break;
                case ReasoningMessageChunkEvent { Delta.Length: > 0 } r:
                    yield return new BitAiReasoningDeltaEvent(r.Delta);
                    break;

                // A resumed run replays the start, arguments and end of the call it paused on; Bit already has them.
                case ToolCallStartEvent s when !_calls.ContainsKey(s.ToolCallId):
                    _calls[s.ToolCallId] = new ToolCall();
                    yield return new BitAiToolCallStartedEvent(s.ToolCallId, s.ToolCallName);
                    break;
                case ToolCallArgsEvent a when _calls.TryGetValue(a.ToolCallId, out var call) && !call.Ended:
                    call.Arguments.Append(a.Delta);
                    yield return new BitAiToolCallDeltaEvent(a.ToolCallId, a.Delta);
                    break;
                case ToolCallEndEvent end when _calls.TryGetValue(end.ToolCallId, out var call) && !call.Ended:
                    call.Ended = true;
                    yield return new BitAiToolCallCompletedEvent(end.ToolCallId, AgUiMapping.ParseJson(call.Arguments.ToString()));
                    break;
                case ToolCallResultEvent r when Declined.Contains(r.ToolCallId):
                    break;
                case ToolCallResultEvent r:
                    yield return new BitAiToolResultEvent(r.ToolCallId, AgUiMapping.ToOutcome(r.Content));
                    break;

                // A custom event with a registered component becomes a part of the message; any other passes through.
                case CustomEvent c when c.Name is not null && components.HasEvent(c.Name):
                    yield return new BitAiStructuredDataEvent(c.Value ?? AgUiMapping.JsonNull) { SchemaName = c.Name };
                    break;
                case CustomEvent c:
                    yield return new BitAiCustomEvent(c.Name ?? "", c.Value);
                    break;
            }
        }

        public BitAiTokenUsage AddUsage(TokenUsage last)
        {
            // Each run reports its own usage; the latest run's input already covers the whole thread.
            _usage = new BitAiTokenUsage
            {
                InputTokens = last.InputTokens,
                OutputTokens = (_usage.OutputTokens ?? 0) + (last.OutputTokens ?? 0),
                CachedInputTokens = last.CachedInputTokens,
                ReasoningTokens = last.ReasoningTokens,
            };
            return _usage;
        }

        private sealed class ToolCall
        {
            public System.Text.StringBuilder Arguments { get; } = new();

            public bool Ended { get; set; }
        }
    }

    internal sealed record PendingApproval(string InterruptId, string ToolCallId, string? Message);

    /// <summary>The agent endpoint answered with an HTTP error.</summary>
    internal sealed class AgUiHttpException(HttpStatusCode status, string? reason)
        : Exception($"The agent endpoint answered {(int)status} {reason}.")
    {
        public HttpStatusCode Status { get; } = status;
    }
}
