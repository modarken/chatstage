namespace ChatStage;

/// <summary>Options for the ChatStage chat: where the agent is and what the UI should know about it.</summary>
public sealed class ChatStageOptions
{
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
