using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Server;
using ChatStage.Server;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatStage.IntegrationTests;

/// <summary>
/// A real Agent Framework server, in memory: a ChatClientAgent over <see cref="FakeChatClient"/>, served by
/// <see cref="ChatStageServerExtensions.MapChatStageAgent"/>. Its tools record every call they make.
/// </summary>
internal sealed class AgentServer : IAsyncDisposable
{
    public const string AccessToken = "test-token";

    /// <summary>What the user is told when a run fails with <see cref="UserFacingException"/>.</summary>
    public const string UserFacingMessage = "You're not signed in to the auction site. Sign in and try again.";

    /// <summary>The message of an internal failure, which must never reach the client.</summary>
    public const string InternalSecret = "Connection failed: Server=db01;Password=hunter2";

    /// <summary>The note <c>NotingAgent</c> leaves in the session before it fails.</summary>
    public const string NoteText = "The last reply was cut off; tools may have acted.";

    /// <summary>The generic message the AG-UI hosting sends for a failure it isn't told to describe.</summary>
    public const string GenericError = "An error occurred while streaming the agent response.";

    private readonly WebApplication _app;

    private AgentServer(WebApplication app, FakeChatClient model, ToolLog tools)
    {
        _app = app;
        Model = model;
        Tools = tools;
    }

    public FakeChatClient Model { get; }

    public ToolLog Tools { get; }

    public TestServer TestServer => _app.GetTestServer();

    public HttpClient CreateClient()
    {
        var client = TestServer.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", AccessToken);
        return client;
    }

    public static async Task<AgentServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddChatStageServer();

        var model = new FakeChatClient();
        var tools = new ToolLog();
        var agent = model.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "demo",
            ChatOptions = new ChatOptions
            {
                Instructions = "Test agent.",
                Tools =
                [
                    AIFunctionFactory.Create(() => "12:00", "get_time", "Gets the time."),
                    new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                        ([Description("The item")] string itemId) => tools.Record("delete_item", itemId), "delete_item", "Deletes an item.")),
                    new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                        ([Description("Why")] string reason) => tools.Record("wipe_everything", reason), "wipe_everything", "Deletes everything.")),
                    AIFunctionFactory.Create(
                        (string query) => new Listing("lot-1187", $"Vintage {query}", 84.50m), "find_listing", "Finds a listing."),
                    AIFunctionFactory.Create(() => "invoice ready", "show_invoice", "Shows the invoice."),
                ],
            },
        });

        var app = builder.Build();

        // A stand-in for real authentication: every run must carry the bearer token.
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.Authorization != $"Bearer {AccessToken}")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });

        // An app-defined AG-UI CUSTOM event, sent after the show_invoice tool's result.
        var stream = new AGUIStreamOptions().MapResult("show_invoice", _ =>
            [new CustomEvent { Name = "demo:invoice", Value = JsonSerializer.SerializeToElement(new { number = "INV-7", total = 12.5m }) }]);
        app.MapChatStageAgent("/agent", new NotingAgent(agent), o => o.DescribeError = ex => ex is UserFacingException ? ex.Message : null).WithMetadata(stream);

        await app.StartAsync();
        return new AgentServer(app, model, tools);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    public sealed record Listing(string Id, string Title, decimal Price);

    /// <summary>An app's exception whose message is written for the user.</summary>
    public sealed class UserFacingException(string message) : Exception(message);

    public sealed record Note(string Text);

    /// <summary>
    /// An agent that keeps state in its session: "fail: with note" leaves a note in the session's state bag and then fails
    /// with a user-facing exception, as an agent that cut a reply short would; "what's the note?" answers with the note.
    /// </summary>
    private sealed class NotingAgent(AIAgent inner) : DelegatingAIAgent(inner)
    {
        public const string NoteKey = "demo.note";

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            switch (list.LastOrDefault()?.Text)
            {
                case "fail: with note":
                    session!.StateBag.SetValue(NoteKey, new Note(NoteText));
                    await Task.Yield();
                    throw new UserFacingException(UserFacingMessage);
                case "what's the note?":
                    var note = session!.StateBag.TryGetValue<Note>(NoteKey, out var found) ? found!.Text : "no note";
                    yield return new AgentResponseUpdate(ChatRole.Assistant, note) { MessageId = "note-" + Guid.NewGuid().ToString("N")[..8] };
                    yield break;
            }

            await foreach (var update in base.RunCoreStreamingAsync(list, session, options, cancellationToken))
            {
                yield return update;
            }
        }
    }

    /// <summary>Every tool call that actually ran, with its argument.</summary>
    public sealed class ToolLog
    {
        public ConcurrentQueue<(string Tool, string Argument)> Calls { get; } = new();

        public string Record(string tool, string argument)
        {
            Calls.Enqueue((tool, argument));
            return $"{tool}({argument})";
        }
    }
}
