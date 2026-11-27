using System.Text.Json;
using Bit.BlazorUI;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChatStage.Blazor.Tests;

/// <summary>
/// <see cref="ChatStageActions.ShowAsync{TData}"/>: the app's own component in the transcript, with no agent run, and never
/// sent to the agent.
/// </summary>
public sealed class ShowAsyncTests
{
    private const string Matches = "acme:matches";

    private static ServiceProvider Services(ScriptedHandler handler, bool serverKeepsHistory = true)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddChatStage(o =>
            {
                o.Endpoint = new Uri("http://agent.test/agent");
                o.ServerKeepsHistory = serverKeepsHistory;
            })
            .AddEventComponent<MatchesCard, MatchList>(Matches)
            .ConfigureJson(o => o.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
            .HttpClient.ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static string Reply(string text) => Sse.Stream(Sse.RunStarted(), Sse.TextStart(), Sse.Text(text), Sse.TextEnd(), Sse.RunFinished());

    private static MatchList SomeMatches() => new([new("lot-1", "Leica M6"), new("lot-2", "Rolleiflex 2.8F")]);

    [Fact]
    public async Task TheComponentRendersWithItsDataAndTheText()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var provider = Services(new ScriptedHandler());
        await using var scope = provider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<BitAiChatSessionFactory>();
        var conversation = new BitAiConversation("c");
        await using var session = factory.Create(conversation, factory.Defaults);
        var actions = ChatStageActions.For(session, provider.GetRequiredService<ChatStageComponentRegistry>());

        var message = Assert.IsType<BitAiMessage>(await actions.ShowAsync(Matches, SomeMatches(), "Matching lots:", ct));

        Assert.Same(message, conversation.LastMessage);
        Assert.Equal(BitAiRole.Assistant, message.Role);
        Assert.True(ChatStageActions.IsAppMessage(message));
        Assert.Equal("Matching lots:", message.GetText());
        var part = Assert.Single(message.Parts.OfType<BitAiStructuredDataPart>());
        Assert.Equal("Leica M6", part.Value?.GetProperty("lots")[0].GetProperty("lot_title").GetString()); // ConfigureJson's options

        var renderers = scope.ServiceProvider.GetRequiredService<BitAiRendererRegistry>();
        var fragment = renderers.FindPart(part.Kind)!(new BitAiMessagePartContext(part, message, conversation, session));
        var html = await RenderAsync(scope.ServiceProvider, fragment);
        Assert.Contains("<li>lot-1: Leica M6</li>", html, StringComparison.Ordinal);
        Assert.Contains("<li>lot-2: Rolleiflex 2.8F</li>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheNextRunDoesntSendIt(bool serverKeepsHistory)
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHandler().Reply(Reply("Hi there.")).Reply(Reply("Sure."));
        await using var provider = Services(handler, serverKeepsHistory);
        await using var scope = provider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<BitAiChatSessionFactory>();
        await using var session = factory.Create(new BitAiConversation("c"), factory.Defaults);
        var actions = ChatStageActions.For(session, provider.GetRequiredService<ChatStageComponentRegistry>());

        await session.SubmitAsync("hello", null, ct);
        await actions.ShowAsync(Matches, SomeMatches(), "Matching lots:", ct);
        await session.SubmitAsync("watch the first one", null, ct);

        var sent = handler.Requests[1].GetProperty("messages").EnumerateArray().ToList();
        var body = handler.Requests[1].GetRawText();
        Assert.DoesNotContain("Matching lots", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Leica", body, StringComparison.Ordinal);
        if (serverKeepsHistory)
        {
            Assert.Equal(["watch the first one"], sent.Select(m => m.GetProperty("content").GetString()));
        }
        else
        {
            Assert.Equal(["user", "assistant", "user"], sent.Select(m => m.GetProperty("role").GetString()));
            Assert.Equal("Hi there.", sent[1].GetProperty("content").GetString());
        }
    }

    [Fact]
    public async Task AnUnregisteredNameIsRejected()
    {
        await using var provider = Services(new ScriptedHandler());
        await using var scope = provider.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<BitAiChatSessionFactory>();
        await using var session = factory.Create(new BitAiConversation("c"), factory.Defaults);
        var actions = ChatStageActions.For(session, provider.GetRequiredService<ChatStageComponentRegistry>());

        Assert.Throws<ArgumentException>(() => { _ = actions.ShowAsync("acme:unknown", SomeMatches(), cancellationToken: TestContext.Current.CancellationToken); });
    }

    [Fact]
    public async Task AChatWithoutASessionCantShow()
    {
        var actions = ChatStageActions.For(null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.ShowAsync(Matches, SomeMatches(), cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task<string> RenderAsync(IServiceProvider services, RenderFragment fragment)
    {
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<FragmentHost>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(FragmentHost.Fragment)] = fragment }));
            return root.ToHtmlString();
        });
    }

    public sealed record Lot(string Id, string LotTitle);

    public sealed record MatchList(IReadOnlyList<Lot> Lots);

    /// <summary>A minimal app component: one line per lot.</summary>
    private sealed class MatchesCard : ChatStageComponent<MatchList>
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "ul");
            foreach (var lot in Data?.Lots ?? [])
            {
                builder.OpenElement(1, "li");
                builder.AddContent(2, $"{lot.Id}: {lot.LotTitle}");
                builder.CloseElement();
            }

            builder.CloseElement();
        }
    }

    private sealed class FragmentHost : ComponentBase
    {
        [Parameter]
        public RenderFragment? Fragment { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Fragment);
    }
}
