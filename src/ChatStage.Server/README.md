# ChatStage.Server

The server side of [ChatStage](https://github.com/modarken/chatstage): serves a Microsoft Agent Framework agent over
AG-UI so a ChatStage chat (or any AG-UI client) can use it.

```csharp
builder.Services.AddChatStageServer();          // AG-UI server + a session store for threads
// ...
app.MapChatStageAgent("/agent", agent);          // agent: any named AIAgent, e.g. chatClient.AsAIAgent(...)
```

It serves the agent the way the Agent Framework's `MapAGUIServer` does (the same request, events, session store,
isolation and `AGUIStreamOptions`), and adds:

- **Threads are kept on the server** (an `AgentSessionStore`; in memory by default, register your own to persist them),
  so clients send only new messages.
- **Approvals are bound to what the agent asked.** A client answers an approval with its id and yes or no; the call
  that runs is the one the thread recorded, with its recorded arguments. A tampered resume (changed arguments, a
  different tool, another thread's id) runs nothing it shouldn't. The Agent Framework's approval response binding stays on.
- **Unanswered approvals are declined** when the user moves on with a new message (after Stop, or a restart).
- **Failed runs are logged**, and by default the client gets only a generic error. Mark the exceptions whose message
  is written for the user and it reaches the chat as the AG-UI `RUN_ERROR` message:

  ```csharp
  app.MapChatStageAgent("/agent", agent, o => o.DescribeError = ex => ex is UserFacingException ? ex.Message : null);
  ```
- **A failed run's session is saved before the error is sent**, so state the agent set before it threw survives, and the
  next run can't overtake the save.

Documentation: <https://github.com/modarken/chatstage>
