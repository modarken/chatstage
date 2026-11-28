using System.Text.Json;

namespace ChatStage;

/// <summary>An interrupt restored from the app's authenticated server history.</summary>
/// <param name="Id">The server's interrupt id.</param>
/// <param name="ToolCallId">The server's tool call id.</param>
/// <param name="Name">The display tool name.</param>
/// <param name="Arguments">The recorded arguments, for display only.</param>
/// <param name="Message">The approval question.</param>
public sealed record ChatStagePendingApproval(string Id, string ToolCallId, string Name, JsonElement Arguments, string? Message = null);
