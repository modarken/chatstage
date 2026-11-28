# ChatStage

[![CI](https://github.com/modarken/chatstage/actions/workflows/ci.yml/badge.svg)](https://github.com/modarken/chatstage/actions/workflows/ci.yml)
[![ChatStage.Blazor on NuGet](https://img.shields.io/nuget/vpre/ChatStage.Blazor?label=ChatStage.Blazor)](https://www.nuget.org/packages/ChatStage.Blazor)
[![ChatStage.Server on NuGet](https://img.shields.io/nuget/vpre/ChatStage.Server?label=ChatStage.Server)](https://www.nuget.org/packages/ChatStage.Server)

A drop-in AI chat UI for .NET apps, and the small server piece that goes with it.

- **The chat** is [Bit.BlazorUI.AI](https://blazorui.bitplatform.dev)'s components (streaming Markdown, tool cards,
  approval prompts, a context meter) talking [AG-UI](https://docs.ag-ui.com) to your agent. It runs wherever Blazor
  does: WPF and WinForms (BlazorWebView), .NET MAUI, and the web.
- **Your own Razor components inside the chat**, for a tool's result or an AG-UI custom event, and they can talk back to
  the agent.
- **Tool approvals done properly**: the server runs only the call the agent asked about, with its recorded arguments,
  whatever a client sends back.

```
 WPF / MAUI / web ── <ChatStageChat>  (Bit.BlazorUI.AI)
                         │  AgUiTransport          ← ChatStage.Blazor
                         │  AG-UI over HTTP + SSE
 ASP.NET Core ─────── MapChatStageAgent("/agent")  ← ChatStage.Server
                         │  ApprovalBindingAgent + session store
                      Microsoft Agent Framework agent → any IChatClient
```

| Package | What it is |
|---|---|
| [`ChatStage.Blazor`](https://www.nuget.org/packages/ChatStage.Blazor) | The chat UI: registration, the AG-UI transport, `<ChatStageChat>`, custom components. |
| [`ChatStage.Server`](https://www.nuget.org/packages/ChatStage.Server) | Serves a Microsoft Agent Framework agent over AG-UI, with threads kept and approvals bound. |

Both target **.NET 11** only.

## Quick start

### 1. The server

```csharp
// dotnet add package ChatStage.Server
using ChatStage.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddChatStageServer();

IChatClient model = /* any IChatClient: OpenAI, Anthropic, Azure OpenAI, Ollama... */;
var agent = model.AsAIAgent(new ChatClientAgentOptions
{
    Name = "assistant",
    ChatOptions = new()
    {
        Instructions = "You are a helpful assistant.",
        Tools =
        [
            AIFunctionFactory.Create(FindListing, "find_listing"),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(DeleteItem, "delete_item")), // asks the user first
        ],
    },
});

var app = builder.Build();
app.MapChatStageAgent("/agent", agent);   // add .RequireAuthorization() for a real deployment
app.Run();
```

### 2. The chat (WPF shown; MAUI and Blazor are the same apart from the host)

```csharp
// dotnet add package ChatStage.Blazor
var services = new ServiceCollection();
services.AddWpfBlazorWebView();
services.AddChatStage(o =>
{
    o.Endpoint = new Uri("https://localhost:5001/agent");
    o.ContextWindow = 200_000;   // for the context meter
})
.AddToolComponent<ListingCard, Listing>("find_listing");
Resources.Add("services", services.BuildServiceProvider());
```

```razor
<ChatStageChat Title="Assistant" EmptyTitle="How can I help?" />
```

The host page loads Bit's assets (and ChatStage's stylesheet, needed for a custom layout):

```html
<link rel="stylesheet" href="_content/Bit.BlazorUI/styles/bit.blazorui.css" />
<link rel="stylesheet" href="_content/Bit.BlazorUI.AI/styles/bit.blazorui.ai.css" />
<link rel="stylesheet" href="_content/ChatStage.Blazor/chatstage.css" />
<link rel="stylesheet" href="{YourApp}.styles.css" />
<script src="_content/Bit.BlazorUI/scripts/bit.blazorui.js"></script>
<script src="_content/Bit.BlazorUI.AI/scripts/bit.blazorui.ai.js"></script>
```

> **WPF:** target a versioned Windows TFM such as `net11.0-windows10.0.19041.0`. BlazorWebView's WebView2 control
> fails at run time on plain `net11.0-windows`.

A complete, runnable example is in [`samples/`](samples): an agent server with a free fake model, and a WPF app with a
custom card.

## Extension points

### Your own component for a tool's result

```razor
@* ListingCard.razor *@
@inherits ChatStageComponent<Listing>

@if (Data is null)
{
    <span>Searching…</span>
}
else
{
    <div>@Data.Title, @Data.Price</div>
    <button disabled="@(!Chat.CanSend)" @onclick="Watch">Watch</button>
}

@code {
    private Task Watch() => Chat.SendAsync($"watch {Data!.Id}");
}
```

```csharp
services.AddChatStage(...).AddToolComponent<ListingCard, Listing>("find_listing");
```

`ChatStageComponent<TData>` gives the component:

| Parameter | |
|---|---|
| `Data` | The tool's JSON result, read into `TData` (`null` until the tool has returned). |
| `ToolCall` | The call: status (running, completed, rejected, failed), arguments and raw result. |
| `Chat` | `ChatStageActions`: `SendAsync(text)` sends a user message to the agent; `ShowAsync(...)` shows a component without it. |

### Your own component for an AG-UI custom event

The agent can send a `CUSTOM` event (for example with `AGUIStreamOptions.MapResult` or `MapContent` on the server).
Register a component for its name and it appears in the reply:

```csharp
services.AddChatStage(...).AddEventComponent<InvoiceCard, Invoice>("acme:invoice");
```

Custom events with no registered component are passed through to Bit as `BitAiCustomEvent`.

### Talking back to the agent

`ChatStageActions.SendAsync` sends a user message. A message sent while the agent is still replying is **queued and
sent when the reply ends**, in order; nothing is dropped. `IsBusy`, `QueuedCount` and the `Changed` event let a component
show that it's waiting. Outside a component, `<ChatStageChat>`'s `Actions` (or `ChatStageActions.For(session)`) gives
the same object.

### Showing your own component without the agent

`ChatStageActions.ShowAsync` puts a component in the transcript from app code, with no agent run: for example a
"View matches" button that shows the matching lots as listing cards. The component is the one registered with
`AddEventComponent` for that name.

```csharp
services.AddChatStage(...).AddEventComponent<MatchesCard, MatchList>("acme:matches");
```

```razor
<ChatStageChat @ref="_chat" />
<button @onclick="ShowMatches">View matches</button>

@code {
    private ChatStageChat _chat = default!;

    private async Task ShowMatches() =>
        await _chat.Actions.ShowAsync("acme:matches", await Lots.FindMatchesAsync(), "Lots matching your watchlist:");
}
```

- It appends an assistant message with the optional text and the component, whose `Data` is the value you passed
  (serialized with the `ConfigureJson` options, so the message can be saved with the conversation).
- **It is never sent to the agent**, as history or otherwise, whether `ServerKeepsHistory` is `true` or `false`.
  `ChatStageActions.IsAppMessage(message)` tells such a message apart.
- Like `SendAsync`, it waits while the agent is replying and keeps its place in the queue, so it never lands inside a
  reply. A component inside the chat can call it too, through its `Chat` parameter.

### Your own layout

`<ChatStageChat>` is Bit's chat with a header. To lay the chat out yourself, without Bit's bubbles, avatars, toolbars
or header, use the building blocks. They keep the AG-UI transport, streaming Markdown, your tool and event components,
and the standard tool cards and secure approvals; nothing is replaced but the markup.

```razor
<ChatStageSession @ref="_chat" @bind-Conversation="_conversation">
    <ChildContent Context="chat">
        <ChatStageTranscript>
            <LabelTemplate Context="message">[@(message.Role.IsUser ? "You" : "Sage")]:</LabelTemplate>
        </ChatStageTranscript>
        @if (chat.IsBusy)
        {
            <button class="chatstage-stop" @onclick="chat.Stop">Stop</button>
        }
        else
        {
            <ChatStageComposer Placeholder="Message Sage" @bind-InputText="_draft" />
        }
    </ChildContent>
</ChatStageSession>
```

| Component | What it is |
|---|---|
| `ChatStageSession` | Renderless: owns the session for `Conversation` and hands its layout a `ChatStageSessionContext`. Renders no element. |
| `ChatStageTranscript` | The scrolling message list (Bit's, with its virtualization). Follows new content only while the reader is at the bottom; `IsAtBottom`, `ScrollToLatestAsync`, `PinnedChanged`. `MessageTemplate`, `LabelTemplate`, `EmptyTemplate`. |
| `ChatStageMessage` | One message, flat: optional `LabelTemplate` (inline with the first paragraph), the parts (Markdown, tool cards, approvals, your components), optional `MetadataTemplate`. `PartTemplate` replaces how each part is drawn. |
| `ChatStageComposer` | A plain textarea and Send (Stop while busy). `@bind-InputText`, `SubmitOnEnter` (Shift+Enter is a newline), `Placeholder`, `SendText`, `StopText`, `Focused`, `Blurred`. Replace it with your own whenever you like. |

`<ChatStageChat Unstyled="true">` gives the same chrome-free layout in one tag, and `ChatStageChat` also takes
`TranscriptTemplate`, `MessageTemplate`, `ComposerTemplate`, `HeaderTemplate` and `EmptyTemplate`, plus
`ShowHeader`, `ShowSuggestions`, `ShowAttachments`, `ShowContextMeter` and `ShowUsage`. Timestamps, message toolbars,
usage and model names in Bit's layout are `UiOptions` switches (`ShowTimestamps`, `ShowMessageActions`, ...). Every
component takes `Class`, `Style` and any other attribute for its root element.

**The context** (`ChatStageSessionContext`, also `ChatStageChat.Context`) is the controller a custom composer or
layout uses, with no DOM access or reflection:

| Member | |
|---|---|
| `Conversation`, `Session`, `Actions` | The thread, Bit's session (approvals, retry) and `ChatStageActions`. |
| `IsBusy`, `PendingApprovals`, `Changed` | Busy while a submission or reply is in progress; the open approval requests; raised on every change (often, possibly off the UI thread). |
| `InputText` | The draft. Bind your input to it. |
| `SubmitAsync(text?, attachments?, ct)` | Sends the draft (or the given text and attachments). Returns `false`, keeping the draft, when busy, empty or cancelled. |
| `Submitting` | `async (submission, ct) => ...` runs first: change `Text` or `Attachments` (after an upload, a slash command), or set `Cancel`. |
| `MessageSent`, `Escape` | Raised once the message is in the transcript; raised by the composer on Escape (close your popup). |
| `Stop()`, `FocusInputAsync()`, `FocusInput` | Stop the reply; focus the composer (a custom composer sets `FocusInput`). |
| `ReplaceMessageText(text, messageId?)` | Replaces a reply's prose, keeping its tool cards and approvals. |

`ChatStageSession` and `ChatStageChat` also have `Stop()`, `FocusInputAsync()` and `NewConversationAsync()` (it raises
`ConversationChanged`). History is hydration: build a `BitAiConversation` with the thread's id and its messages and hand
it to `Conversation`; a different instance switches threads (the old session is stopped and disposed).

A complete custom layout (flat labels, a slash-command popup, only Stop while streaming) is
[`samples/ChatStage.Samples.Wpf/CustomChat.razor`](samples/ChatStage.Samples.Wpf/CustomChat.razor).

### Styling

`_content/ChatStage.Blazor/chatstage.css` styles the chrome-free layout through these classes and variables, which are
ChatStage's public styling contract. Set the variables on any ancestor; restyle the classes freely.

| Part | Class | Variables |
|---|---|---|
| Layout root | `chatstage-headless` (Unstyled) / `chatstage` | |
| Transcript | `chatstage-transcript`, `chatstage-transcript-content` | `--chatstage-transcript-padding`, `--chatstage-message-gap`, `--chatstage-scrollbar-thumb`, `--chatstage-scrollbar-track`, `--chatstage-scrollbar-width` |
| Selection | | `--chatstage-selection-background`, `--chatstage-selection-color` |
| Message | `chatstage-message` (`data-role="user"`/`"assistant"`), `chatstage-message-labelled` | `--chatstage-message-padding`, `--chatstage-message-background`, `--chatstage-line-height` |
| Label, body, metadata | `chatstage-label`, `chatstage-body`, `chatstage-metadata` | `--chatstage-label-color`, `--chatstage-user-label-color`, `--chatstage-label-weight`, `--chatstage-label-gap`, `--chatstage-body-color`, `--chatstage-metadata-size`, `--chatstage-metadata-color`, `--chatstage-metadata-opacity` |
| Markdown | `chatstage-text`, `chatstage-markdown`, `chatstage-prose`, `chatstage-paragraph`, `chatstage-heading`, `chatstage-list`, `chatstage-list-item`, `chatstage-link`, `chatstage-inline-code`, `chatstage-caret` | `--chatstage-paragraph-margin`, `--chatstage-heading-1-size` … `-3-size`, `--chatstage-heading-small-size`, `--chatstage-heading-weight`, `--chatstage-heading-margin`, `--chatstage-list-marker`, `--chatstage-ordered-list-marker`, `--chatstage-list-indent`, `--chatstage-list-margin`, `--chatstage-code-background`, `--chatstage-code-color`, `--chatstage-code-font`, `--chatstage-code-size`, `--chatstage-code-padding`, `--chatstage-code-radius`, `--chatstage-inline-code-background`, `--chatstage-link-color`, `--chatstage-caret-color` |
| Tool cards, approvals | `chatstage-tool`, `chatstage-tool-card`, `chatstage-tool-header`, `chatstage-tool-body`, `chatstage-approval` | `--chatstage-tool-margin`, `--chatstage-tool-background`, `--chatstage-tool-border`, `--chatstage-tool-color`, `--chatstage-approval-background`, `--chatstage-approval-color` |
| Composer | `chatstage-composer` (`data-busy`), `chatstage-input`, `chatstage-send`, `chatstage-stop` | `--chatstage-composer-gap`, `--chatstage-composer-padding`, `--chatstage-input-color`, `--chatstage-input-background`, `--chatstage-input-caret`, `--chatstage-input-border`, `--chatstage-input-padding`, `--chatstage-input-radius`, `--chatstage-placeholder-color`, `--chatstage-button-padding`, `--chatstage-send-color`, `--chatstage-send-background`, `--chatstage-stop-color`, `--chatstage-stop-background` |

With a `LabelTemplate`, the label runs into the first paragraph (`[Sage]: Hello…`). `ChatStageMessage`'s
`TextClasses`/`TextStyles` and `ToolClasses`/`ToolStyles`, and `ChatStageChat`'s `Classes`/`Styles`, take Bit's own
class and style parts for anything finer.

### Final text from the server

A server that settles a reply's text only at the end (for example resolving image or attachment references) can send
it in a `CUSTOM` event after the last text delta and before `RUN_FINISHED`:

```json
{ "type": "CUSTOM", "name": "acme:final-text", "value": { "text": "Here it is: ![chart](/media/42.png)" } }
```

```csharp
services.AddChatStage(o =>
{
    o.Endpoint = new Uri("https://localhost:5001/agent");
    o.FinalText = e => e.Name == "acme:final-text" && e.Data is JsonElement data ? data.GetProperty("text").GetString() : null;
});
```

The text replaces the streamed prose of the reply, in any layout; tool cards, approvals and reasoning stay. App code can
do the same with `ChatStageSessionContext.ReplaceMessageText`.

### Approvals after a reload

A reload, or a restart of a desktop app, loses an open Allow / Decline card. If your server can tell you which
approvals are still open on a thread (their interrupt id, tool call id, tool name, arguments and question), show them
again with the standard card:

```razor
@inject AgUiTransport Transport

<ChatStageChat @ref="_chat" Conversation="_restoredConversation" />

@code {
    private ChatStageChat _chat = default!;

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender && _openApprovals.Count > 0)
        {
            // Completes when the user has answered and the resumed run has finished, so don't await it here.
            _ = Transport.RestoreApprovalsAsync(_chat.Session!, _openApprovals);
        }
    }
}
```

`ChatStagePendingApproval(Id, ToolCallId, Name, Arguments, Message)` is one approval. The cards are drawn locally;
nothing is sent until the user answers, and then only the usual resume (interrupt id, decision, tool call id) goes to
the server, which must run the call **it** recorded (as `ChatStage.Server` does). The name and arguments are only shown.
Several approvals resume together; Stop declines them on the thread's next run, as for a live run. The session must be
idle.

### Errors the user should see

When a run fails, the chat shows the `message` of the AG-UI `RUN_ERROR` event that ended it (or "The agent run
failed." when it is empty). That is the whole contract, so it works with any AG-UI server: send a `RUN_ERROR` whose
`message` is written for the user.

`MapChatStageAgent` sends only the AG-UI hosting's generic message ("An error occurred while streaming the agent
response."), so no internal detail leaks. To let your own exceptions through, describe them:

```csharp
app.MapChatStageAgent("/agent", agent, o =>
    o.DescribeError = ex => ex is UserFacingException ? ex.Message : null);
```

`DescribeError` returns the text for the user, or `null` to keep the exception private (the generic message is sent).
Every failed run is still logged on the server. Whether described or not, a failed run's session is **saved before
the `RUN_ERROR` is sent**, so state the agent set before it threw (for example a note in `session.StateBag`) is there
for the next run, and the client can't start that run before the save.

### Authentication

```csharp
services.AddChatStage(...)
    .AddBearerToken(async (sp, ct) => await sp.GetRequiredService<ITokenSource>().GetTokenAsync(ct))
    .AddHeaders((sp, ct) => ValueTask.FromResult<IEnumerable<KeyValuePair<string, string>>>([new("X-Api-Key", key)]));
```

Both are called for every request, so tokens can refresh. For anything else, `ChatStageBuilder.HttpClient` is the
`IHttpClientBuilder` for the chat's named `HttpClient`: add your own `DelegatingHandler`, resilience or a primary handler.
A 401 or 403 shows in the chat as an authentication or access error.

### JSON

`ConfigureJson(o => ...)` changes how component data is read (default: web defaults, camelCase).

### Options

| `ChatStageOptions` | |
|---|---|
| `Endpoint` | The agent's AG-UI endpoint. Required. |
| `ContextWindow` | Tokens, for the context meter. |
| `ModelId` | Shown with each reply. |
| `ServerKeepsHistory` | Default `true`: each run sends only the new message (the server keeps the thread). Set `false` for a stateless AG-UI server. |
| `ResumeRetries`, `ResumeRetryDelay` | Retries for an approval's resume when the connection fails (default 2, from 500 ms). |
| `UiOptions` | Bit's `BitAiUiOptions` for the chat: image policy, timestamps, message actions, Enter behavior, strings... Default `null`: Bit's defaults. A component's own `UiOptions` parameter wins. |
| `RenderInterval` | The minimum time between renders while a reply streams (default `null`: `UiOptions.RenderInterval`, 50 ms). |
| `FinalText` | Maps a `CUSTOM` event to the reply's final text (see above). |

For example, images only from your own host, others behind a click to load, no inline `data:` images:

```csharp
o.UiOptions = BitAiUiOptions.Default with
{
    Images = BitAiImagePolicy.AllowListedHosts("app.example.com") with { AllowDataImages = false },
};
```

Bit's allow-list is by host: a relative URL, or another host, is shown after a click. For strictly same-origin images,
also send the CSP `img-src 'self'` (the browser then refuses any other source).

Everything else in Bit.BlazorUI.AI (conversation stores, themes through `BitThemeManager`, strings, the model catalog)
works as Bit documents it. `<ChatStageChat>` exposes its `Session`; use `<BitAiChat>` directly for full control.

### Rendering while a reply streams

Bit's views render the streamed text themselves, at most once per `RenderInterval`, re-parsing only the Markdown that
changed. `ChatStageChat` and `ChatStageSession` render their own layout through one Bit `BitAiRenderScheduler`: never
for a streamed delta, coalesced for other conversation changes, and at once for a tool, approval, error, usage or
completion event, a change of busy state and the end of a turn.

Measured with [`samples/ChatStage.Samples.OverlayProbe`](samples/ChatStage.Samples.OverlayProbe) (Release, .NET 11,
Windows; a 640x480 transparent, off-screen window; 5 s of streaming at one delta every ~2.5 ms; CPU in percent of one
core; private bytes of the app and its WebView2 processes; local synthetic numbers, not guarantees):

| Scenario | Layout renders | App CPU | App + WebView2 CPU | Private MiB |
|---|---:|---:|---:|---:|
| 0.2 wrapper, conversation `Changed` on every delta | 1952 | 75.5% | 111.2% | 389 |
| 0.3, conversation `Changed` on every delta | 101 | 49.9% | 68.7% | 374 |
| 0.3, text only, BlazorWebView (composition) | 4 | 40.3% | 55.2% | 345 |
| 0.3, text only, Blazor Server page in a `WebView2CompositionControl` | 7 | 41.2% | 55.8% | 362 |

### Transparent WPF windows (overlays)

A WPF window with `AllowsTransparency="True"` can't show the default `BlazorWebView` (an HWND control). Turn on its
composition mode, which renders through `WebView2CompositionControl`, in process, with no web server or Blazor
Server circuit:

```xml
<Window AllowsTransparency="True" WindowStyle="None" Background="Transparent" ...>
    <blazor:BlazorWebView UseCompositionControl="True" HostPage="wwwroot/index.html" Services="{DynamicResource services}">
        ...
    </blazor:BlazorWebView>
</Window>
```

and give the page a transparent background (`html, body, #app { background: transparent; }`). `UseCompositionControl`
is part of `Microsoft.AspNetCore.Components.WebView.Wpf` (11.0); `BlazorWebViewInitializing` sets the browser's user
data folder, profile and options. That control does not accept an existing `CoreWebView2Environment`, so it can't be
made to share a browser process created elsewhere in the app; ChatStage doesn't add a host of its own for that. In the
probe above, the in-process composition host used about as much CPU as a Blazor Server page in a composition control,
and less memory.

### Hosting under a sub-path (`/ui/`)

Serve the app at the sub-path and set the base, keeping every asset path relative (no leading `/`):

```html
<base href="/ui/" />
<link rel="stylesheet" href="_content/ChatStage.Blazor/chatstage.css" />
<link rel="stylesheet" href="{YourApp}.styles.css" />
```

Don't set `StaticWebAssetBasePath` for this in a WebAssembly app: the SDK then writes the imports of the scoped-CSS
bundle as `../_content/...`, which miss under the base and lose the components' styles. If you need it anyway, link
`_content/ChatStage.Blazor/chatstage.css` as above: it carries the default chat's layout too.

A Content Security Policy for a WebAssembly host needs at least:

```
default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:;
connect-src 'self' https://your-agent.example.com; base-uri 'self'; object-src 'none'
```

`'wasm-unsafe-eval'` runs the .NET runtime, `base-uri 'self'` allows the `<base>` element, `connect-src` must include
the agent's endpoint, and Bit's views use inline styles. Narrow `img-src` to what your replies may show.

## How approvals work

1. The agent calls a tool wrapped in `ApprovalRequiredAIFunction`. The Agent Framework records a
   `ToolApprovalRequestContent` in the thread and the AG-UI run ends with an `interrupt`.
2. The chat shows Bit's Allow / Decline prompt on the tool card, and keeps the reply open.
3. The answer goes back as an AG-UI `resume` with only the approval id, yes or no, and the tool call id.
4. `ApprovalBindingAgent` (in `ChatStage.Server`) matches the answer to the request **recorded in that thread** and
   rebuilds the response from it, so the recorded call runs with its recorded arguments. A tampered resume (changed
   arguments, a different tool, another thread's id, an invented id, a repeat) runs nothing it shouldn't; the tests in
   [`tests/ChatStage.IntegrationTests`](tests/ChatStage.IntegrationTests) cover each case.
5. If the user presses Stop or the app restarts while a prompt is open, the approval is declined when the user sends
   their next message, and the thread carries on.

`MapChatStageAgent` needs a session store (`AddChatStageServer()` registers an in-memory one; register your own
`AgentSessionStore` to keep threads across restarts). The Agent Framework's approval response binding stays on. A host
with several users must authenticate the endpoint and register an `AgentIsolationKeyProvider` so each user's threads
are kept apart.

## Versions and dependencies

- Versions follow [SemVer](https://semver.org) and come from git tags through [MinVer](https://github.com/adamralph/minver).
- **Bit.BlazorUI is prerelease** and pinned to exactly `11.0.0-pre-25` (see [`Directory.Packages.props`](Directory.Packages.props)).
  The AG-UI hosting of the Agent Framework is also a preview. ChatStage wraps both; while they are prerelease,
  ChatStage releases are prereleases too.

## Building

```sh
dotnet build
dotnet test
```

The .NET SDK version is pinned in [`global.json`](global.json). Tests use xUnit v3 on Microsoft.Testing.Platform and
need no network or API keys. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Releasing

1. Move the **Unreleased** notes in [CHANGELOG.md](CHANGELOG.md) under the new version, and merge to `main`.
2. Tag and push: `git tag v0.2.0 && git push origin v0.2.0` (a tag with a suffix, such as `v0.2.0-preview.1`, makes a
   prerelease).
3. The [Release workflow](.github/workflows/release.yml) builds, tests, packs, publishes both packages to nuget.org with
   [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (no API key is stored),
   and creates the GitHub Release with the packages attached.

## License

[MIT](LICENSE)
