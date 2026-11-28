namespace ChatStage;

/// <summary>Options for the ChatStage chat: where the agent is and what the UI should know about it.</summary>
public sealed class ChatStageOptions
{
    /// <summary>
    /// Bit.BlazorUI.AI's presentation and security options for the chat (image policy, timestamps, message actions,
    /// Enter behavior, strings...). <see langword="null"/> (the default) keeps Bit's registered defaults.
    /// </summary>
    /// <remarks>
    /// Used by <c>ChatStageChat</c>, <c>ChatStageSession</c> and <c>ChatStageTranscript</c> unless a component is handed
    /// its own <c>UiOptions</c>. For example, images from your own host only, others behind a click:
    /// <c>o.UiOptions = BitAiUiOptions.Default with { Images = BitAiImagePolicy.AllowListedHosts("app.example.com") }</c>.
    /// </remarks>
    public Bit.BlazorUI.BitAiUiOptions? UiOptions { get; set; }

    /// <summary>
    /// The minimum time between renders while a reply streams, for the chat's layout and Bit's views. Overrides
    /// <see cref="Bit.BlazorUI.BitAiUiOptions.RenderInterval"/>; <see langword="null"/> (the default) keeps that one (50 ms).
    /// </summary>
    public TimeSpan? RenderInterval { get; set; }

    /// <summary>
    /// Maps an AG-UI <c>CUSTOM</c> event to the final Markdown of the reply being streamed, or returns
    /// <see langword="null"/> to leave the text alone. The returned text replaces the reply's prose; tool cards, approvals
    /// and reasoning are kept.
    /// </summary>
    /// <remarks>
    /// Send the event after the last text delta and before <c>RUN_FINISHED</c>. Applied by <c>ChatStageChat</c> and
    /// <c>ChatStageSession</c>, in any layout.
    /// </remarks>
    public Func<Bit.BlazorUI.BitAiCustomEvent, string?>? FinalText { get; set; }

    /// <summary>The UI options in force: <see cref="UiOptions"/> (or Bit's registered ones) with <see cref="RenderInterval"/>.</summary>
    internal Bit.BlazorUI.BitAiUiOptions ResolveUi(Bit.BlazorUI.BitAiUiOptions? registered)
    {
        var ui = UiOptions ?? registered ?? Bit.BlazorUI.BitAiUiOptions.Default;
        return RenderInterval is { } interval && interval != ui.RenderInterval ? ui with { RenderInterval = interval } : ui;
    }

    /// <summary>The agent's AG-UI endpoint, for example <c>https://localhost:5001/agent</c>. Required.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>The model's context window in tokens, for the context meter. <see langword="null"/> when unknown.</summary>
    public int? ContextWindow { get; set; }

    /// <summary>The model's id, shown with each reply. Optional.</summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// <see langword="true"/> (the default) when the server keeps each thread's history, as an Agent Framework server with a
    /// session store does (and <c>ChatStage.Server</c> always does): each run then sends only the new user message.
    /// <see langword="false"/> sends the whole transcript every run, for stateless servers.
    /// </summary>
    public bool ServerKeepsHistory { get; set; } = true;

    /// <summary>
    /// How many times an approval's resume run is retried when the connection fails before the server answers.
    /// Retrying is safe against a ChatStage server: an answer it already has is ignored. Default 2.
    /// </summary>
    public int ResumeRetries { get; set; } = 2;

    /// <summary>The delay before the first retry of a resume run; it doubles for each further retry. Default 500 ms.</summary>
    public TimeSpan ResumeRetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}
