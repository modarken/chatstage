using System.Net;
using System.Text;
using System.Text.Json;

namespace ChatStage.Blazor.Tests;

/// <summary>Builds AG-UI server-sent event streams and serves them from a fake HTTP handler.</summary>
internal static class Sse
{
    public static string Event(object payload) => "data: " + JsonSerializer.Serialize(payload) + "\n\n";

    public static string Stream(params object[] events) => string.Concat(events.Select(Event));

    public static object RunStarted(string thread = "t", string run = "r") => new { type = "RUN_STARTED", threadId = thread, runId = run };

    public static object RunFinished(string thread = "t", string run = "r") => new { type = "RUN_FINISHED", threadId = thread, runId = run };

    public static object Text(string delta, string id = "m1") => new { type = "TEXT_MESSAGE_CONTENT", messageId = id, delta };

    public static object TextStart(string id = "m1") => new { type = "TEXT_MESSAGE_START", messageId = id, role = "assistant" };

    public static object TextEnd(string id = "m1") => new { type = "TEXT_MESSAGE_END", messageId = id };

    public static object ToolStart(string id, string name) => new { type = "TOOL_CALL_START", toolCallId = id, toolCallName = name };

    public static object ToolArgs(string id, string delta) => new { type = "TOOL_CALL_ARGS", toolCallId = id, delta };

    public static object ToolEnd(string id) => new { type = "TOOL_CALL_END", toolCallId = id };

    public static object ToolResult(string id, string content) => new { type = "TOOL_CALL_RESULT", messageId = "r-" + id, toolCallId = id, content, role = "tool" };

    public static object Custom(string name, object value) => new { type = "CUSTOM", name, value };

    public static object Error(string message, string code = "E") => new { type = "RUN_ERROR", message, code };

    public static object Interrupt(string interruptId, string toolCallId, string message = "Allow?", string thread = "t", string run = "r") => new
    {
        type = "RUN_FINISHED",
        threadId = thread,
        runId = run,
        outcome = new { type = "interrupt", interrupts = new[] { new { id = interruptId, reason = "tool_call", message, toolCallId } } },
    };

    public static object Usage(int input, int output, string thread = "t", string run = "r") => new
    {
        type = "RUN_FINISHED",
        threadId = thread,
        runId = run,
        usage = new[] { new { model = "m", inputTokens = input, outputTokens = output } },
    };
}

/// <summary>Answers each POST with the next scripted response and records the request bodies.</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<JsonElement> Requests { get; } = [];

    public List<HttpRequestMessage> RequestMessages { get; } = [];

    public ScriptedHandler Reply(string sse)
    {
        _responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });
        return this;
    }

    public ScriptedHandler Status(HttpStatusCode status)
    {
        _responses.Enqueue(() => new HttpResponseMessage(status));
        return this;
    }

    public ScriptedHandler Throw()
    {
        _responses.Enqueue(() => throw new HttpRequestException("connection reset"));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestMessages.Add(request);
        Requests.Add(JsonElement.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)));
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No scripted response left.");
        }

        return _responses.Dequeue()();
    }
}
