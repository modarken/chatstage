using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace ChatStage.IntegrationTests;

/// <summary>
/// A deterministic model: it picks a reply from keywords in the last user message, streams text in pieces, calls tools
/// the way a real model would, and reports token usage. It records every request it receives.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    public List<List<ChatMessage>> Requests { get; } = [];

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        lock (Requests)
        {
            Requests.Add(list);
        }

        var messageId = "msg-" + Guid.NewGuid().ToString("N")[..8];
        var inputTokens = list.Sum(m => m.Text.Length + 40) / 4;
        await Task.Yield();

        // "fail: ..." makes the run throw: an exception written for the user, or one that must stay on the server.
        switch (list.LastOrDefault()?.Text)
        {
            case "fail: user-facing":
                yield return new ChatResponseUpdate(ChatRole.Assistant, "Checking your account") { MessageId = messageId };
                throw new AgentServer.UserFacingException(AgentServer.UserFacingMessage);
            case "fail: internal":
                throw new InvalidOperationException(AgentServer.InternalSecret);
        }

        // A tool has just answered: describe its result.
        if (list.LastOrDefault()?.Contents.OfType<FunctionResultContent>().LastOrDefault() is { } result)
        {
            var text = result.Result?.ToString() is { } r && r.Contains("reject", StringComparison.OrdinalIgnoreCase)
                ? "OK, I did not do that."
                : $"Done: {result.Result}.";
            foreach (var update in Text(text, messageId))
            {
                yield return update;
            }

            yield return Usage(inputTokens, text.Length / 4, messageId);
            yield break;
        }

        var prompt = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text.ToUpperInvariant() ?? "";
        (string Name, Dictionary<string, object?> Args)? call =
            prompt.Contains("DELETE", StringComparison.Ordinal) ? ("delete_item", new() { ["itemId"] = "demo-42" })
            : prompt.Contains("TIME", StringComparison.Ordinal) ? ("get_time", [])
            : prompt.Contains("LISTING", StringComparison.Ordinal) ? ("find_listing", new() { ["query"] = "camera" })
            : prompt.Contains("INVOICE", StringComparison.Ordinal) ? ("show_invoice", [])
            : null;
        if (call is { } c)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call_" + Guid.NewGuid().ToString("N")[..8], c.Name, c.Args)]) { MessageId = messageId };
            yield return Usage(inputTokens, 10, messageId);
            yield break;
        }

        var reply = prompt.StartsWith("WATCH", StringComparison.Ordinal)
            ? "Watching it."
            : "## Hello\n\nThis is **streamed** Markdown:\n\n```csharp\nvar x = 1;\n```\n";
        foreach (var update in Text(reply, messageId))
        {
            yield return update;
        }

        yield return Usage(inputTokens, reply.Length / 4, messageId);
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private static IEnumerable<ChatResponseUpdate> Text(string text, string messageId)
    {
        for (var i = 0; i < text.Length; i += 8)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, text.Substring(i, Math.Min(8, text.Length - i))) { MessageId = messageId };
        }
    }

    private static ChatResponseUpdate Usage(int input, int output, string messageId) =>
        new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output })])
        {
            MessageId = messageId,
        };
}
