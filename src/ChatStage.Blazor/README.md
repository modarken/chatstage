# ChatStage.Blazor

A drop-in AI chat UI for .NET apps. It shows [Bit.BlazorUI.AI](https://blazorui.bitplatform.dev) chat components
and talks [AG-UI](https://docs.ag-ui.com) to your agent, so it works with any AG-UI server, including a
Microsoft Agent Framework agent served by [ChatStage.Server](https://www.nuget.org/packages/ChatStage.Server).
It runs wherever Blazor does: the web, WPF and WinForms (BlazorWebView) and .NET MAUI.

```csharp
services.AddChatStage(o => o.Endpoint = new Uri("https://localhost:5001/agent"))
        .AddToolComponent<ListingCard, Listing>("find_listing")   // your own component for a tool's result
        .AddBearerToken((sp, ct) => sp.GetRequiredService<ITokenSource>().GetTokenAsync(ct));
```

```razor
<ChatStageChat Title="Assistant" />
```

- Streaming Markdown, tool cards, approvals (Allow / Decline), a context meter and token usage.
- Your own Razor components inside the chat, for tool results and AG-UI `CUSTOM` events, with a way to talk back to the agent.
- Messages a component sends while the agent is replying are queued, not dropped.

Bit.BlazorUI is a prerelease series; this package pins one exact build of it.

Documentation: <https://github.com/modarken/chatstage>
