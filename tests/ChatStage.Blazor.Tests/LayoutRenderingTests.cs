using Bit.BlazorUI;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace ChatStage.Blazor.Tests;

public sealed class LayoutRenderingTests
{
    [Fact]
    public async Task RenderlessLayoutContainsInlineLabelAndStandardToolApprovalWithoutDefaultChrome()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IJSRuntime, NoScript>();
        services.AddChatStage(o => o.Endpoint = new Uri("https://agent.test/agent"));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<CustomLayout>();
            return root.ToHtmlString();
        });
        Assert.Contains("[Assistant]:", html, StringComparison.Ordinal);
        Assert.Contains("chatstage-paragraph", html, StringComparison.Ordinal);
        Assert.Contains("chatstage-approval", html, StringComparison.Ordinal);
        Assert.Contains("chatstage-tool-card", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chatstage-header", html, StringComparison.Ordinal);
        Assert.DoesNotContain("bit-aicmg", html, StringComparison.Ordinal);
    }

    private sealed class CustomLayout : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            var conversation = new BitAiConversation("restored");
            var message = conversation.AddMessage(BitAiRole.Assistant, "Hello **world**.");
            var call = new BitAiToolCallPart("call", "delete");
            call.CompleteArguments();
            call.SetStatus(BitAiToolCallStatus.AwaitingApproval);
            message.AddPart(call);
            builder.OpenComponent<ChatStageSession>(0);
            builder.AddAttribute(1, nameof(ChatStageSession.Conversation), conversation);
            builder.AddAttribute(2, nameof(ChatStageSession.ChildContent), (RenderFragment<ChatStageSessionContext>)(_ => content =>
            {
                content.OpenComponent<ChatStageTranscript>(0);
                content.AddAttribute(1, nameof(ChatStageTranscript.LabelTemplate), (RenderFragment<BitAiMessage>)(_ => label => label.AddContent(0, "[Assistant]: ")));
                content.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private sealed class NoScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
