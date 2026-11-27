using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ChatStage;

/// <summary>Configures ChatStage after <see cref="ChatStageServiceCollectionExtensions.AddChatStage"/>: custom components and authentication.</summary>
public sealed class ChatStageBuilder
{
    private readonly ChatStageComponentRegistry _components;

    internal ChatStageBuilder(IServiceCollection services, IHttpClientBuilder httpClient, ChatStageComponentRegistry components)
    {
        Services = services;
        HttpClient = httpClient;
        _components = components;
    }

    /// <summary>The service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// The HTTP client the chat posts its runs with. Add your own <see cref="DelegatingHandler"/>s here (for example
    /// <c>.AddHttpMessageHandler&lt;MyAuthHandler&gt;()</c>), or configure resilience.
    /// </summary>
    public IHttpClientBuilder HttpClient { get; }

    /// <summary>
    /// Shows <typeparamref name="TComponent"/> in the chat for every call of the tool <paramref name="toolName"/>, in place
    /// of the standard tool card. The tool's JSON result is read into <see cref="ChatStageComponent{TData}.Data"/>.
    /// </summary>
    /// <typeparam name="TComponent">Your component.</typeparam>
    /// <typeparam name="TData">The type the tool's result is read into.</typeparam>
    /// <param name="toolName">The tool's name, as the agent declares it.</param>
    /// <returns>This builder.</returns>
    public ChatStageBuilder AddToolComponent<TComponent, TData>(string toolName)
        where TComponent : ChatStageComponent<TData>
    {
        _components.AddTool(toolName, typeof(TComponent), typeof(TData));
        return this;
    }

    /// <summary>
    /// Shows <typeparamref name="TComponent"/> in the chat for every AG-UI <c>CUSTOM</c> event named
    /// <paramref name="eventName"/>. The event's value is read into <see cref="ChatStageComponent{TData}.Data"/>.
    /// </summary>
    /// <typeparam name="TComponent">Your component.</typeparam>
    /// <typeparam name="TData">The type the event's value is read into.</typeparam>
    /// <param name="eventName">The custom event's name, for example <c>acme:invoice</c>.</param>
    /// <returns>This builder.</returns>
    public ChatStageBuilder AddEventComponent<TComponent, TData>(string eventName)
        where TComponent : ChatStageComponent<TData>
    {
        _components.AddEvent(eventName, typeof(TComponent), typeof(TData));
        return this;
    }

    /// <summary>
    /// Changes how custom components' data is read from tool results and custom event values (by default: web defaults,
    /// camelCase, case-insensitive). Use it to add converters or a source-generated context.
    /// </summary>
    /// <param name="configure">Changes the serializer options.</param>
    /// <returns>This builder.</returns>
    public ChatStageBuilder ConfigureJson(Action<JsonSerializerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_components.Json);
        return this;
    }

    /// <summary>
    /// Sends <c>Authorization: Bearer &lt;token&gt;</c> with every run. The provider is called for each request, so it can
    /// return a refreshed token; returning <see langword="null"/> sends no header.
    /// </summary>
    /// <param name="tokenProvider">Gets the current token.</param>
    /// <returns>This builder.</returns>
    public ChatStageBuilder AddBearerToken(Func<IServiceProvider, CancellationToken, ValueTask<string?>> tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        HttpClient.AddHttpMessageHandler(sp => new HeaderHandler(async ct =>
            await tokenProvider(sp, ct).ConfigureAwait(false) is { Length: > 0 } token
                ? [new("Authorization", "Bearer " + token)]
                : []));
        return this;
    }

    /// <summary>Adds headers to every run, for example an API key. The provider is called for each request.</summary>
    /// <param name="headerProvider">Gets the headers to send.</param>
    /// <returns>This builder.</returns>
    public ChatStageBuilder AddHeaders(Func<IServiceProvider, CancellationToken, ValueTask<IEnumerable<KeyValuePair<string, string>>>> headerProvider)
    {
        ArgumentNullException.ThrowIfNull(headerProvider);
        HttpClient.AddHttpMessageHandler(sp => new HeaderHandler(ct => headerProvider(sp, ct)));
        return this;
    }

    private sealed class HeaderHandler(Func<CancellationToken, ValueTask<IEnumerable<KeyValuePair<string, string>>>> headers) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var (name, value) in await headers(cancellationToken).ConfigureAwait(false))
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
