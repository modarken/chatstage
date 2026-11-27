using System.Runtime.CompilerServices;
using AGUI.Abstractions;
using AGUI.Server;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace ChatStage.Server;

/// <summary>
/// The AG-UI endpoint behind <c>MapChatStageAgent</c>. It is the Agent Framework's <c>MapAGUIServer</c> pipeline, built
/// from the same public pieces (the AG-UI SDK's event conversion and the hosting's session store and isolation), with two
/// differences for a run that fails: its error message can be made user-facing (<see cref="ChatStageAgentOptions.DescribeError"/>),
/// and its session is saved before the client is told, rather than after.
/// </summary>
internal static partial class ChatStageAgentEndpoint
{
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder endpoints, string pattern, AIAgent agent, ChatStageAgentOptions options, ILogger logger)
    {
        var describe = options.DescribeError;
        return endpoints.MapPost(pattern, async Task<IResult> ([FromBody] RunAgentInput? input, IOptions<HttpJsonOptions> json, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (input is null)
            {
                return Results.BadRequest();
            }

            var host = HostAgent(context.RequestServices, agent);
            var streamOptions = context.GetEndpoint()?.Metadata.GetMetadata<AGUIStreamOptions>()
                ?? context.RequestServices.GetService<IOptions<AGUIStreamOptions>>()?.Value;
            var request = input.ToChatRequestContext(json.Value.SerializerOptions, streamOptions);
            var threadId = string.IsNullOrWhiteSpace(request.Input.ThreadId) ? Guid.NewGuid().ToString("N") : request.Input.ThreadId;
            request.Input.ThreadId = threadId;
            var session = await host.GetOrCreateSessionAsync(threadId, cancellationToken).ConfigureAwait(false);

            var failure = new Failure();
            var updates = Observe(host.RunStreamingAsync(request.Messages, session, new ChatClientAgentRunOptions { ChatOptions = request.ChatOptions }, cancellationToken), failure, cancellationToken);
            var events = updates.AsChatResponseUpdatesAsync().AsAGUIEventStreamAsync(request, cancellationToken);
            return TypedResults.ServerSentEvents(CompleteAsync(events, failure, describe, host, threadId, session, logger, cancellationToken));
        });
    }

    /// <summary>The agent with the thread's session store, scoped to the caller's isolation key, as <c>MapAGUIServer</c> builds it.</summary>
    private static AIHostAgent HostAgent(IServiceProvider services, AIAgent agent)
    {
        var store = services.GetKeyedService<AgentSessionStore>(agent.Name);
        var isolation = services.GetService<AgentIsolationKeyProvider>();
        if (store?.GetService<IsolationKeyScopedAgentSessionStore>() is null)
        {
            store = new IsolationKeyScopedAgentSessionStore(
                store ?? new NoopAgentSessionStore(),
                isolation,
                new IsolationKeyScopedAgentSessionStoreOptions { Strict = isolation is not null });
        }

        return new AIHostAgent(agent, store, agent.Name);
    }

    /// <summary>Passes the agent's updates through, noting the exception that ends the run, if any.</summary>
    private static async IAsyncEnumerable<AgentResponseUpdate> Observe(IAsyncEnumerable<AgentResponseUpdate> updates, Failure failure, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var enumerator = updates.GetAsyncEnumerator(cancellationToken);
        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield break;
                    }
                }
                catch (Exception ex) when (failure.Record(ex))
                {
                    throw; // not reached: Record returns false, so the exception propagates unchanged
                }

                yield return enumerator.Current;
            }
        }
    }

    /// <summary>
    /// Passes the run's events through and saves the thread's session. A run that ended with an exception is saved
    /// <em>before</em> its <c>RUN_ERROR</c> is sent, with the exception's user-facing description (if any) as the message:
    /// state the agent set before it threw is kept, and the client can't start the next run before the save.
    /// </summary>
    private static async IAsyncEnumerable<BaseEvent> CompleteAsync(IAsyncEnumerable<BaseEvent> events, Failure failure, Func<Exception, string?>? describe, AIHostAgent host, string threadId, AgentSession session, ILogger logger, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var saved = false;
        await foreach (var e in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (e is RunErrorEvent error && failure.Exception is { } exception && !saved)
            {
                if (describe is not null && Description(exception, describe, logger) is { } message)
                {
                    error.Message = message;
                }

                saved = true;
                await SaveFailedRunAsync(host, threadId, session, logger, cancellationToken).ConfigureAwait(false);
            }

            yield return e;
        }

        if (!saved)
        {
            await host.SaveSessionAsync(threadId, session, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SaveFailedRunAsync(AIHostAgent host, string threadId, AgentSession session, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await host.SaveSessionAsync(threadId, session, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The run has already failed: a failed save is logged, and the client still gets the run's error.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            LogSaveFailed(logger, threadId, ex);
        }
    }
    private static string? Description(Exception exception, Func<Exception, string?> describe, ILogger logger)
    {
        try
        {
            return describe(exception) is { } message && !string.IsNullOrWhiteSpace(message) ? message : null;
        }
#pragma warning disable CA1031 // A failing describer must not break the error report: the client gets the generic message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDescribeFailed(logger, ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Saving thread {ThreadId} after its run failed threw; the client still gets the run's error")]
    private static partial void LogSaveFailed(ILogger logger, string threadId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "DescribeError threw; the client gets the generic error message")]
    private static partial void LogDescribeFailed(ILogger logger, Exception exception);

    private sealed class Failure
    {
        public Exception? Exception { get; private set; }

        public bool Record(Exception exception)
        {
            Exception ??= exception;
            return false;
        }
    }
}
