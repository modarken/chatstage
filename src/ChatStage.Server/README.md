# ChatStage.Server

The server side of [ChatStage](https://github.com/modarken/chatstage): serves a Microsoft Agent Framework agent over
AG-UI so a ChatStage chat (or any AG-UI client) can use it.

```csharp
builder.Services.AddChatStageServer();          // AG-UI server + a session store for threads
// ...
app.MapChatStageAgent("/agent", agent);          // agent: any named AIAgent, e.g. chatClient.AsAIAgent(...)
```

What it adds over a bare `MapAGUIServer`:

- **Threads are kept on the server** (an `AgentSessionStore`; in memory by default, register your own to persist them),
  so clients send only new messages.
- **Approvals are bound to what the agent asked.** A client answers an approval with its id and yes or no; the call
  that runs is the one the thread recorded, with its recorded arguments. A tampered resume (changed arguments, a
  different tool, another thread's id) runs nothing it shouldn't. The Agent Framework's approval response binding stays on.
- **Unanswered approvals are declined** when the user moves on with a new message (after Stop, or a restart).
- **Failed runs are logged**; AG-UI only tells the client that the run failed.

Documentation: <https://github.com/modarken/chatstage>
