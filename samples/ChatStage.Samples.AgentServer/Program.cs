using System.ComponentModel;
using ChatStage.Samples.AgentServer;
using ChatStage.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// A minimal agent server for the samples: one Agent Framework agent with three tools, served over AG-UI.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddChatStageServer();

var agent = new DemoChatClient().AsAIAgent(new ChatClientAgentOptions
{
    Name = "demo",
    ChatOptions = new ChatOptions
    {
        Instructions = "You are a demo assistant. Use tools when asked. Answer in Markdown.",
        Tools =
        [
            AIFunctionFactory.Create(GetTime, "get_time"),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(DeleteItem, "delete_item")), // asks the user first
            AIFunctionFactory.Create(FindListing, "find_listing"),                                 // shown as the app's ListingCard
        ],
    },
});

var app = builder.Build();
app.MapChatStageAgent("/agent", agent);
app.MapGet("/", () => "ChatStage sample agent. AG-UI endpoint: POST /agent");
app.Run();

[Description("Gets the server's current time.")]
static string GetTime() => DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

[Description("Deletes a demo item. Destructive, so the user approves it first.")]
static string DeleteItem([Description("The item's id")] string itemId) => $"deleted {itemId}";

[Description("Finds a marketplace listing.")]
static Listing FindListing([Description("What to look for")] string query) =>
    new("lot-1187", $"Vintage {query} (Pentax K1000)", 84.50m, "USD");

/// <summary>A listing, as the find_listing tool returns it.</summary>
internal sealed record Listing(string Id, string Title, decimal Price, string Currency);
