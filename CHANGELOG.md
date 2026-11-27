# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `ChatStage.Blazor`: the chat UI. `AddChatStage()` wires Bit.BlazorUI.AI to an AG-UI agent through `AgUiTransport`;
  `<ChatStageChat>` is a ready-made chat with a context meter and token usage.
- Custom components: `AddToolComponent<TComponent, TData>()` for a tool's result and
  `AddEventComponent<TComponent, TData>()` for an AG-UI `CUSTOM` event, built on `ChatStageComponent<TData>`.
- `ChatStageActions`: components send messages back to the agent; a message sent while the agent replies is queued.
- Pluggable authentication: `AddBearerToken()`, `AddHeaders()`, or any handler on `ChatStageBuilder.HttpClient`.
- Approvals: a resume carries only the approval id, the decision and the tool call id; a resume that fails to connect
  is retried; approvals left open by Stop are declined on the thread's next run.
- `ChatStage.Server`: `AddChatStageServer()` and `MapChatStageAgent()` serve an Agent Framework agent over AG-UI with
  each thread kept in a session store, and `ApprovalBindingAgent` binds approval answers to the calls the thread recorded.

### Dependencies

- Bit.BlazorUI and Bit.BlazorUI.AI pinned to exactly `11.0.0-pre-25` (prerelease).
- Microsoft.Agents.AI.Hosting.AGUI.AspNetCore `1.28.0-preview.261106.1` (preview).

[Unreleased]: https://github.com/modarken/chatstage/commits/main
