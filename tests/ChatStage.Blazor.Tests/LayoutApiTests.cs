using System.Runtime.CompilerServices;
using System.Text.Json;
using Bit.BlazorUI;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace ChatStage.Blazor.Tests;

// The public layout API: thread switching, the composer controller, attachments on the wire, restored approvals and
// the unchanged default chat. Rendered with the HtmlRenderer, so nothing opens a window or takes the focus.
public sealed class LayoutApiTests
{
    [Fact]
    public async Task DefaultChatMarkupIsUnchanged()
    {
        await using var host = await Host.CreateAsync();
        var html = await host.RenderAsync<ChatStageChat>(new Dictionary<string, object?> { [nameof(ChatStageChat.Title)] = "Assistant" });
        Assert.StartsWith("<div class=\"chatstage\" data-testid=\"chatstage\" b-", html, StringComparison.Ordinal);
        Assert.Matches("<header class=\"chatstage-header\" b-[a-z0-9]+><span class=\"chatstage-title\" b-[a-z0-9]+>Assistant</span>", html);
        Assert.Contains("bit-aicht", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chatstage-headless", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chatstage-composer", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChromeCanBeTurnedOffAndRootTakesClassStyleAndAttributes()
    {
        await using var host = await Host.CreateAsync();
        var html = await host.RenderAsync<ChatStageChat>(new Dictionary<string, object?>
        {
            [nameof(ChatStageChat.Unstyled)] = true,
            [nameof(ChatStageChat.ShowHeader)] = false,
            [nameof(ChatStageChat.Class)] = "overlay",
            [nameof(ChatStageChat.Style)] = "opacity:.9",
            ["data-app"] = "sage",
        });
        Assert.Contains("class=\"chatstage-headless overlay\"", html, StringComparison.Ordinal);
        Assert.Contains("style=\"opacity:.9\"", html, StringComparison.Ordinal);
        Assert.Contains("data-app=\"sage\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chatstage-header", html, StringComparison.Ordinal);
        Assert.DoesNotContain("bit-aicht", html, StringComparison.Ordinal);
        Assert.Contains("chatstage-transcript", html, StringComparison.Ordinal);
        Assert.Contains("chatstage-input", html, StringComparison.Ordinal);
        Assert.Contains("chatstage-send", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewConversationSurvivesAnUnboundParentAndAParameterSwitchesThreads()
    {
        await using var host = await Host.CreateAsync();
        var first = new BitAiConversation("first");
        var probe = new ProbeState { Conversation = first };
        await host.RenderAsync(probe);
        var firstSession = probe.Session!.Session!;
        Assert.Same(first, probe.Session.Context!.Conversation);

        BitAiConversation? started = null;
        probe.Changed = c => started = c;
        await host.Renderer.Dispatcher.InvokeAsync(probe.Session.NewConversationAsync);
        Assert.NotNull(started);
        Assert.Same(started, probe.Session.Context!.Conversation);
        Assert.NotSame(firstSession, probe.Session.Session);

        // The parent re-renders with its old, unbound value: the new thread stays.
        await host.Renderer.Dispatcher.InvokeAsync(probe.Refresh);
        Assert.Same(started, probe.Session.Context!.Conversation);

        // A new value from the parent switches to that thread.
        var second = new BitAiConversation("second");
        probe.Conversation = second;
        await host.Renderer.Dispatcher.InvokeAsync(probe.Refresh);
        Assert.Same(second, probe.Session.Context!.Conversation);
    }

    [Fact]
    public async Task AsynchronousInterceptionTransformsPromptAndAttachmentsAndClearsTheDraftOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var transport = new RecordingTransport();
        await using var session = new BitAiChatSession(transport, new("t"), new() { ExecuteTools = false, InterruptOnSubmit = false, Pacing = null });
        using var context = new ChatStageSessionContext(session) { InputText = "/img cat" };
        var file = new BitAiAttachment("cat.png", "image/png") { Url = "https://files.test/cat.png" };
        var sentCount = 0;
        context.MessageSent += _ => sentCount++;
        context.Submitting = async (submission, token) =>
        {
            await Task.Delay(20, token); // an upload, or a slash command looked up on a server
            submission.Text = submission.Text.Replace("/img ", "Describe: ", StringComparison.Ordinal);
            submission.Attachments = [file];
        };

        Assert.True(await context.SubmitAsync(cancellationToken: ct));
        Assert.Equal(1, sentCount);
        Assert.Empty(context.InputText);
        var user = transport.Request!.Messages[^1];
        Assert.Equal("Describe: cat", user.GetText());
        Assert.Same(file, Assert.Single(user.Attachments));
    }

    [Fact]
    public async Task ExplicitTextLeavesTheDraftAlone()
    {
        var transport = new RecordingTransport();
        await using var session = new BitAiChatSession(transport, new("t"), new() { ExecuteTools = false, InterruptOnSubmit = false, Pacing = null });
        using var context = new ChatStageSessionContext(session) { InputText = "half-typed" };
        Assert.True(await context.SubmitAsync("button text", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("half-typed", context.InputText);
    }

    [Fact]
    public async Task AttachmentsGoOnTheWireByUrlInlineDataOrStagedContent()
    {
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.Text("ok"), Sse.RunFinished()));
        await using var session = new BitAiChatSession(CreateTransport(handler), new("t"), new() { ExecuteTools = false, InterruptOnSubmit = false, Pacing = null });
        BitAiAttachment[] files =
        [
            new("a.png", "image/png") { Url = "https://files.test/a.png" },
            new("b.pdf", "application/pdf") { Data = new byte[] { 1, 2, 3 } },
            new("c.txt", "text/plain") { ContentFactory = _ => ValueTask.FromResult<Stream>(new MemoryStream([4, 5])) },
        ];
        await session.SubmitAsync("look", files, TestContext.Current.CancellationToken);
        var content = Assert.Single(handler.Requests).GetProperty("messages")[0].GetProperty("content");
        var parts = content.EnumerateArray().ToArray();
        Assert.Equal(4, parts.Length);
        Assert.Equal("look", parts[0].GetProperty("text").GetString());
        Assert.Equal("https://files.test/a.png", parts[1].GetProperty("source").GetProperty("value").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), parts[2].GetProperty("source").GetProperty("value").GetString());
        Assert.Equal(Convert.ToBase64String([4, 5]), parts[3].GetProperty("source").GetProperty("value").GetString());
    }

    [Fact]
    public async Task TextOnlyMessageStaysAPlainString()
    {
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.Text("ok"), Sse.RunFinished()));
        await using var session = new BitAiChatSession(CreateTransport(handler), new("t"), new() { ExecuteTools = false, InterruptOnSubmit = false, Pacing = null });
        await session.SubmitAsync("hello", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.String, Assert.Single(handler.Requests).GetProperty("messages")[0].GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task SeveralRestoredApprovalsResumeTogether()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.Text("done"), Sse.RunFinished()));
        var transport = CreateTransport(handler);
        await using var session = new BitAiChatSession(transport, new("saved"), new() { ExecuteTools = false, InterruptOnSubmit = false, Pacing = null });
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = 0;
        session.Processor.EventApplied += e => { if (e is BitAiToolApprovalRequestedEvent && ++requested == 2) shown.TrySetResult(); };
        var restore = transport.RestoreApprovalsAsync(session,
        [
            new("i-1", "c-1", "delete", JsonElement.Parse("{}")),
            new("i-2", "c-2", "archive", JsonElement.Parse("{}")),
        ], ct);
        await shown.Task.WaitAsync(ct);
        Assert.Empty(handler.Requests);
        session.Approve("c-2", BitAiApprovalDecision.Rejected);
        session.Approve("c-1", BitAiApprovalDecision.Approved);
        await restore.WaitAsync(ct);
        var resume = Assert.Single(handler.Requests).GetProperty("resume").EnumerateArray().ToArray();
        Assert.Equal(["i-1", "i-2"], resume.Select(r => r.GetProperty("interruptId").GetString()));
        Assert.False(session.IsGenerating);
    }

    [Fact]
    public async Task StoppingARestoredApprovalDeclinesItOnTheNextRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.Text("ok"), Sse.RunFinished()));
        var transport = CreateTransport(handler);
        await using var session = new BitAiChatSession(transport, new("saved"), new() { ExecuteTools = false, InterruptOnSubmit = false, Pacing = null });
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Processor.EventApplied += e => { if (e is BitAiToolApprovalRequestedEvent) shown.TrySetResult(); };
        var restore = transport.RestoreApprovalsAsync(session, [new("i-1", "c-1", "delete", JsonElement.Parse("{}"))], ct);
        await shown.Task.WaitAsync(ct);
        session.Stop();
        await restore.WaitAsync(ct);
        Assert.Empty(handler.Requests);
        Assert.False(session.IsGenerating);

        await session.SubmitAsync("carry on", cancellationToken: ct);
        var request = Assert.Single(handler.Requests);
        var resume = Assert.Single(request.GetProperty("resume").EnumerateArray());
        Assert.Equal("i-1", resume.GetProperty("interruptId").GetString());
        Assert.Contains("false", resume.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreRejectsABusySessionAndDuplicateIds()
    {
        var transport = CreateTransport(new ScriptedHandler());
        await using var session = new BitAiChatSession(transport, new("saved"), new() { ExecuteTools = false, InterruptOnSubmit = false, Pacing = null });
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.RestoreApprovalsAsync(session, [], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.RestoreApprovalsAsync(session,
            [new("i", "c", "a", default), new("i", "d", "b", default)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void UiOptionsFallBackToBitsRegisteredOnesAndRenderIntervalOverrides()
    {
        var registered = BitAiUiOptions.Default with { ShowTimestamps = true };
        Assert.Same(registered, new ChatStageOptions().ResolveUi(registered));
        var own = BitAiUiOptions.Default with { Images = BitAiImagePolicy.AllowListedHosts("app.example.com") with { AllowDataImages = false } };
        Assert.Same(own, new ChatStageOptions { UiOptions = own }.ResolveUi(registered));
        var resolved = new ChatStageOptions { UiOptions = own, RenderInterval = TimeSpan.FromMilliseconds(120) }.ResolveUi(registered);
        Assert.Equal(TimeSpan.FromMilliseconds(120), resolved.RenderInterval);
        Assert.Equal(BitAiImageMode.AllowListedHosts, resolved.Images.Mode);
    }

    [Fact]
    public void NegativeRenderIntervalFailsValidation()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddChatStage(o => { o.Endpoint = new Uri("https://agent.test/agent"); o.RenderInterval = TimeSpan.FromMilliseconds(-1); });
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ChatStageOptions>>().Value);
    }

    [Fact]
    public async Task RunErrorLeavesTheContextIdle()
    {
        var handler = new ScriptedHandler().Reply(Sse.Stream(Sse.RunStarted(), Sse.Error("nope")));
        await using var session = new BitAiChatSession(CreateTransport(handler), new("t"), new() { ExecuteTools = false, InterruptOnSubmit = false, Pacing = null });
        using var context = new ChatStageSessionContext(session) { InputText = "hi" };
        var busy = new List<bool>();
        context.Changed += () => busy.Add(context.IsBusy);
        Assert.True(await context.SubmitAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(context.IsBusy);
        Assert.False(session.Conversation.IsGenerating);
        Assert.False(busy[^1]);
    }

    private static AgUiTransport CreateTransport(ScriptedHandler handler) => new(new HttpClient(handler),
        Options.Create(new ChatStageOptions { Endpoint = new Uri("https://agent.test/agent") }), NullLogger<AgUiTransport>.Instance);

    private sealed class RecordingTransport : IBitAiTransport
    {
        public BitAiRequest? Request { get; private set; }

        public async IAsyncEnumerable<BitAiEvent> StreamAsync(BitAiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Request = request;
            await Task.Yield();
            yield return new BitAiMessageStartedEvent(BitAiRole.Assistant);
            yield return new BitAiTextDeltaEvent("ok");
            yield return new BitAiMessageCompletedEvent(BitAiFinishReason.Stop);
        }
    }

    private sealed class ProbeState
    {
        public BitAiConversation? Conversation { get; set; }
        public Action<BitAiConversation>? Changed { get; set; }
        public SessionProbe? Probe { get; set; }
        public ChatStageSession? Session { get; set; }
        public void Refresh() => Probe!.Refresh();
    }

    private sealed class SessionProbe : ComponentBase
    {
        [Parameter] public ProbeState State { get; set; } = default!;

        public void Refresh() => StateHasChanged();

        protected override void OnInitialized() => State.Probe = this;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ChatStageSession>(0);
            builder.AddAttribute(1, nameof(ChatStageSession.Conversation), State.Conversation);
            builder.AddAttribute(2, nameof(ChatStageSession.ConversationChanged), EventCallback.Factory.Create<BitAiConversation>(this, c => State.Changed?.Invoke(c)));
            builder.AddAttribute(3, nameof(ChatStageSession.ChildContent), (RenderFragment<ChatStageSessionContext>)(_ => _ => { }));
            builder.AddComponentReferenceCapture(4, r => State.Session = (ChatStageSession)r);
            builder.CloseComponent();
        }
    }
    private sealed class Host : IAsyncDisposable
    {
        private ServiceProvider _provider = default!;
        private AsyncServiceScope _scope;

        public HtmlRenderer Renderer { get; private set; } = default!;

        public static Task<Host> CreateAsync()
        {
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<IJSRuntime, NoScript>();
            services.AddChatStage(o => o.Endpoint = new Uri("https://agent.test/agent"));
            var host = new Host { _provider = services.BuildServiceProvider() };
            host._scope = host._provider.CreateAsyncScope();
            host.Renderer = new HtmlRenderer(host._scope.ServiceProvider, host._provider.GetRequiredService<ILoggerFactory>());
            return Task.FromResult(host);
        }

        public Task<string> RenderAsync<T>(Dictionary<string, object?> parameters) where T : IComponent =>
            Renderer.Dispatcher.InvokeAsync(async () => (await Renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());

        public Task RenderAsync(ProbeState state) => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await Renderer.RenderComponentAsync<SessionProbe>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(SessionProbe.State)] = state }));
            await root.QuiescenceTask;
        });

        public async ValueTask DisposeAsync()
        {
            await Renderer.DisposeAsync();
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    private sealed class NoScript : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }
}
