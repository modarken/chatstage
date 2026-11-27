using System.Diagnostics.CodeAnalysis;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ChatStage.Server;

/// <summary>Serves agents to ChatStage chats over AG-UI.</summary>
public static class ChatStageServerExtensions
{
    /// <summary>
    /// Adds the AG-UI server and a session store for agent threads. The default store keeps threads in memory; register
    /// your own <see cref="AgentSessionStore"/> (before or after this call) to keep them across restarts.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddChatStageServer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAGUIServer();
        services.TryAddSingleton<AgentSessionStore, InMemoryAgentSessionStore>();

        // The AG-UI hosting looks for a store keyed by the agent's name; by default every agent uses the one store above.
        // A store registered for one agent's name still takes precedence for that agent.
        services.TryAddKeyedSingleton<AgentSessionStore>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<AgentSessionStore>());
        return services;
    }

    /// <summary>
    /// Serves <paramref name="agent"/> over AG-UI at <paramref name="pattern"/>, with each thread kept in the session store
    /// and tool approvals bound to the calls the agent recorded (see <see cref="ApprovalBindingAgent"/>). Failed runs are
    /// logged, since the AG-UI response carries only a generic error.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">The route, for example <c>/agent</c>.</param>
    /// <param name="agent">The agent. It must have a name: threads are stored under it.</param>
    /// <returns>The endpoint, for <c>RequireAuthorization()</c> and other conventions.</returns>
    /// <remarks>
    /// Thread ids come from the client. A host with several users must authenticate the endpoint and register an
    /// <see cref="AgentIsolationKeyProvider"/> so each user's threads are kept apart.
    /// </remarks>
    public static IEndpointConventionBuilder MapChatStageAgent(this IEndpointRouteBuilder endpoints, [StringSyntax("Route")] string pattern, AIAgent agent)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(agent);
        if (string.IsNullOrWhiteSpace(agent.Name))
        {
            throw new ArgumentException("The agent needs a name: its threads are stored under it.", nameof(agent));
        }

        if (endpoints.ServiceProvider.GetKeyedService<AgentSessionStore>(agent.Name) is null or NoopAgentSessionStore)
        {
            throw new InvalidOperationException(
                $"No session store for the agent '{agent.Name}'. Call services.{nameof(AddChatStageServer)}(), or register an {nameof(AgentSessionStore)}: approvals are checked against each thread's history.");
        }

        var logger = endpoints.ServiceProvider.GetRequiredService<ILogger<ApprovalBindingAgent>>();
        return endpoints.MapAGUIServer(pattern, new ApprovalBindingAgent(agent, logger));
    }
}
