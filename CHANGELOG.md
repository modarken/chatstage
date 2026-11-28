# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.3.0-preview.1] - 2026-11-27

### Added

- `ChatStage.Blazor`: custom layouts from public building blocks. `<ChatStageSession>` (renderless) hands its layout a
  `ChatStageSessionContext`: conversation, session, actions, `IsBusy`, `PendingApprovals`, `Changed`, the draft
  (`InputText`), `SubmitAsync` with `Submitting` interception (change the prompt or attachments, or cancel),
  `MessageSent`, `Escape`, `Stop`, `FocusInputAsync` and `ReplaceMessageText`. `ChatStageTranscript`,
  `ChatStageMessage` (flat, with an inline `LabelTemplate`) and `ChatStageComposer` keep streaming Markdown, your
  components and the standard tool cards and approvals. `NewConversationAsync` and thread switching by `Conversation`.
- `ChatStageChat`: `Unstyled` (the chrome-free layout), `TranscriptTemplate`, `MessageTemplate`, `ComposerTemplate`,
  `HeaderTemplate`, `EmptyTemplate`, `Classes`/`Styles`, `ShowHeader`, `ShowSuggestions`, `ShowAttachments`,
  `UiOptions`, `Class`, `Style` and attribute pass-through, `Context`, `Stop()`, `FocusInputAsync()`,
  `NewConversationAsync()` and `ConversationChanged`.
- `_content/ChatStage.Blazor/chatstage.css`: documented CSS classes and `--chatstage-*` variables (transcript, scrollbars,
  selection, labels, Markdown, code, links, tool cards, approvals, composer, Stop).
- `ChatStageOptions.FinalText`: a `CUSTOM` event can replace a streamed reply's text with the server's final text.
- `AgUiTransport.RestoreApprovalsAsync`: shows the standard Allow / Decline cards for approvals the app restored after a
  reload, and resumes only with the recorded interrupt and tool call ids once answered.
- `ChatStageOptions.UiOptions` (Bit's `BitAiUiOptions`, such as the image policy) and `ChatStageOptions.RenderInterval`.
- README: custom layouts, styling, final text, restored approvals, image policy, hosting under a sub-path with its CSP,
  and transparent WPF windows with `BlazorWebView.UseCompositionControl`.
- Samples: a custom flat layout in the WPF sample, and `ChatStage.Samples.OverlayProbe`, which measures rendering.

### Changed

- `ChatStageChat` renders its own layout through one Bit `BitAiRenderScheduler`, never for a streamed delta and at once
  for tool, approval, error, usage and completion events (it rendered on every conversation change). Measured in the
  README.
- User messages with attachments send them to the agent as AG-UI content parts (by URL, inline data, or the staged
  file's content); they were left out.
- `AgUiTransport` is registered in dependency injection (scoped, as Bit's transport is), so app code can inject it.

### Fixed

- A terminal `RUN_ERROR` leaves the session, the conversation and a layout's busy state idle (covered by tests; the
  stuck flag could not be reproduced with the pinned packages).

## [0.2.0-preview.1] - 2026-11-27

### Added

- `ChatStage.Server`: `MapChatStageAgent(pattern, agent, o => o.DescribeError = ...)` makes chosen exceptions
  user-facing: the message `DescribeError` returns for an exception that ended a run is sent as the AG-UI `RUN_ERROR`
  message. Any other exception still sends only the generic message, so nothing internal leaks.
- `ChatStage.Blazor`: `ChatStageActions.ShowAsync(eventName, data, text)` shows the component registered with
  `AddEventComponent` in the transcript from app code, with no agent run. The message is never sent to the agent, with
  `ServerKeepsHistory` `true` or `false`; `ChatStageActions.IsAppMessage` tells it apart.
- `ChatStageChat.Actions`: the chat's `ChatStageActions`, for app code around the chat.

### Changed

- `ChatStage.Server`: a run that fails has its session saved **before** the `RUN_ERROR` is sent (it was saved after,
  racing the client's disconnect and its next run), so state the agent set before it threw is kept.
- `ChatStage.Server`: `MapChatStageAgent` maps its own AG-UI endpoint, built from the same public pieces as the Agent
  Framework's `MapAGUIServer` (request, events, session store, isolation, `AGUIStreamOptions`).
- `ChatStage.Blazor`: a `RUN_ERROR` with an empty message shows "The agent run failed.". The documented contract for
  any AG-UI server: the chat shows `RUN_ERROR`'s `message`.

### Dependencies

- AGUI.Server `1.0.1`, now referenced directly by `ChatStage.Server` (it was a transitive `1.0.0`).

## [0.1.0-preview.1] - 2026-11-27

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

[Unreleased]: https://github.com/modarken/chatstage/compare/v0.3.0-preview.1...HEAD
[0.3.0-preview.1]: https://github.com/modarken/chatstage/compare/v0.2.0-preview.1...v0.3.0-preview.1
[0.2.0-preview.1]: https://github.com/modarken/chatstage/compare/v0.1.0-preview.1...v0.2.0-preview.1
[0.1.0-preview.1]: https://github.com/modarken/chatstage/releases/tag/v0.1.0-preview.1
