using Bit.BlazorUI;
using ChatStage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers ChatStage.</summary>
public static class ChatStageServiceCollectionExtensions
{
    /// <summary>The name of the <see cref="HttpClient"/> the chat uses (see <see cref="ChatStageBuilder.HttpClient"/>).</summary>
    public const string HttpClientName = "ChatStage";

    /// <summary>
    /// Adds the ChatStage chat: Bit.BlazorUI and Bit.BlazorUI.AI, wired to an AG-UI agent through <see cref="AgUiTransport"/>.
    /// Tools run on the agent's server; the chat shows them and asks for approvals.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options; <see cref="ChatStageOptions.Endpoint"/> is required.</param>
    /// <returns>A builder for custom components and authentication.</returns>
    public static ChatStageBuilder AddChatStage(this IServiceCollection services, Action<ChatStageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var components = services.FirstOrDefault(d => d.ServiceType == typeof(ChatStageComponentRegistry))?.ImplementationInstance as ChatStageComponentRegistry;
        if (components is null)
        {
            components = new ChatStageComponentRegistry();
            services.AddSingleton(components);
        }

        services.AddOptions<ChatStageOptions>()
            .Configure(configure)
            .Validate(o => o.RenderInterval is not { } interval || interval >= TimeSpan.Zero, $"{nameof(ChatStageOptions)}.{nameof(ChatStageOptions.RenderInterval)} must not be negative.")
            .Validate(o => o.Endpoint is { IsAbsoluteUri: true }, $"{nameof(ChatStageOptions)}.{nameof(ChatStageOptions.Endpoint)} must be an absolute URI.")
            .ValidateOnStart();

        // Runs stream for as long as the agent works, so the client has no overall timeout; Stop cancels a run.
        var httpClient = services.AddHttpClient(HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);

        services.AddBitBlazorUIServices();
        services.AddScoped(sp => new AgUiTransport(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            sp.GetRequiredService<IOptions<ChatStageOptions>>(), components,
            sp.GetRequiredService<ILogger<AgUiTransport>>()));
        services.AddBitBlazorUIAIServices(ai =>
        {
            ai.UseTransport(sp => sp.GetRequiredService<AgUiTransport>());

            // The agent runs its tools on the server, and a click on a card is queued rather than interrupting a reply.
            ai.ConfigureSessions(o => o with { ExecuteTools = false, InterruptOnSubmit = false });
            // The app's own components, applied when Bit's renderer registry is first resolved.
            ai.ConfigureRenderers(components.Apply);
        });
        return new ChatStageBuilder(services, httpClient, components);
    }
}
