using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace ChatStage.Samples.AgentServer;

/// <summary>
/// A free, deterministic stand-in for a real model, so the sample runs without an API key. It picks its reply from
/// keywords in the last user message, streams text in pieces and calls tools the way a real model would.
/// Replace it with a real <see cref="IChatClient"/> (OpenAI, Anthropic, Azure, Ollama...) in your own app.
/// </summary>
internal sealed class DemoChatClient : IChatClient
{

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var messageId = "msg-" + Guid.NewGuid().ToString("N")[..8];
        var inputTokens = list.Sum(m => m.Text.Length + 40) / 4;

        string reply;
        if (list.LastOrDefault()?.Contents.OfType<FunctionResultContent>().LastOrDefault() is { } result)
        {
            reply = result.Result?.ToString() is { } r && r.Contains("reject", StringComparison.OrdinalIgnoreCase)
                ? "OK, I did **not** delete anything."
                : result.Result is System.Text.Json.JsonElement
                    ? "Here's what I found. Press **Watch** on the card and I'll keep an eye on it."
                    : $"Done: `{result.Result}`.";
        }
        else
        {
            var prompt = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text.ToUpperInvariant() ?? "";
            (string Name, Dictionary<string, object?> Args)? call =
                prompt.Contains("DELETE", StringComparison.Ordinal) ? ("delete_item", new() { ["itemId"] = "demo-42" })
                : prompt.Contains("TIME", StringComparison.Ordinal) ? ("get_time", [])
                : prompt.Contains("LISTING", StringComparison.Ordinal) || prompt.Contains("FIND", StringComparison.Ordinal) ? ("find_listing", new() { ["query"] = "camera" })
                : null;
            if (call is { } c)
            {
                await Task.Delay(150, cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call_" + Guid.NewGuid().ToString("N")[..8], c.Name, c.Args)]) { MessageId = messageId };
                yield return Usage(inputTokens, 20, messageId);
                yield break;
            }

            reply = prompt.StartsWith("WATCH", StringComparison.Ordinal)
                ? $"Watching **{list[^1].Text[6..]}**. I'll tell you when the price changes."
                : """
                  ## Hello from the demo agent

                  This reply is streamed in pieces. It has **bold**, a list and a code block:

                  - point one
                  - point two

                  ```csharp
                  app.MapChatStageAgent("/agent", agent);
                  ```

                  Try: *what time is it*, *delete the demo item* or *find a listing*.
                  """;
        }

        for (var i = 0; i < reply.Length; i += 12)
        {
            await Task.Delay(20, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply.Substring(i, Math.Min(12, reply.Length - i))) { MessageId = messageId };
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

    private static ChatResponseUpdate Usage(int input, int output, string messageId) =>
        new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = input + output })])
        {
            MessageId = messageId,
        };
}
