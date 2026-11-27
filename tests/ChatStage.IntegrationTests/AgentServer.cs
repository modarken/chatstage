using System.Collections.Concurrent;
using System.ComponentModel;
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
        app.MapChatStageAgent("/agent", agent).WithMetadata(stream);

        await app.StartAsync();
        return new AgentServer(app, model, tools);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    public sealed record Listing(string Id, string Title, decimal Price);

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
