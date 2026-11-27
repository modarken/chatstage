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

The host page loads Bit's assets:

```html
<link rel="stylesheet" href="_content/Bit.BlazorUI/styles/bit.blazorui.css" />
<link rel="stylesheet" href="_content/Bit.BlazorUI.AI/styles/bit.blazorui.ai.css" />
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
| `Chat` | `ChatStageActions`: `SendAsync(text)` sends a user message to the agent. |

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
show that it's waiting. Outside a component, `ChatStageActions.For(session)` gives the same object.

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

Everything else in Bit.BlazorUI.AI (conversation stores, themes through `BitThemeManager`, strings, the model catalog)
works as Bit documents it. `<ChatStageChat>` exposes its `Session`; use `<BitAiChat>` directly for full control.

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
