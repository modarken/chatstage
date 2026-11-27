using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace ChatStage.Samples.Wpf;

/// <summary>The window: a BlazorWebView showing the ChatStage chat.</summary>
public partial class MainWindow : Window
{
    /// <summary>Sets up the services and the view.</summary>
    public MainWindow()
    {
        // The sample agent server (samples/ChatStage.Samples.AgentServer) listens here; CHATSTAGE_AGENT_URL overrides it.
        var endpoint = Environment.GetEnvironmentVariable("CHATSTAGE_AGENT_URL") ?? "http://localhost:5287/agent";

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        services.AddLogging();
        services.AddChatStage(o =>
            {
                o.Endpoint = new Uri(endpoint);
                o.ContextWindow = 8_000;
                o.ModelId = "demo";
            })
            .AddToolComponent<ListingCard, ListingCard.Listing>("find_listing"); // the app's own card for this tool

        Resources.Add("services", services.BuildServiceProvider());
        InitializeComponent();
    }
}
