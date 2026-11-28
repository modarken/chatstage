using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Bit.BlazorUI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Wpf;

namespace ChatStage.Samples.OverlayProbe;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application();
        var window = new Window
        {
            Width = 640, Height = 480, WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Transparent, ShowActivated = false, ShowInTaskbar = false,
            Left = -10000, Top = -10000
        };
        window.Loaded += async (_, _) =>
        {
            try { await RunAsync(window, args); }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { window.Close(); }
        };
        app.Run(window);
    }

    private static void Configure(IServiceCollection services, ProbeState state)
    {
        services.AddLogging();
        services.AddSingleton(state);
        services.AddChatStage(o => o.Endpoint = new Uri("https://unused.invalid/agent"));
        services.AddBitBlazorUIAIServices(ai =>
        {
            ai.UseTransport(new ProbeTransport(state));
            ai.ConfigureSessions(o => o with { Pacing = null, InterruptOnSubmit = false, ExecuteTools = false });
        });
    }

    private static async Task RunAsync(Window window, string[] args)
    {
        var server = args.Contains("server", StringComparer.Ordinal);
        var state = new ProbeState { Before = args.Contains("before", StringComparer.Ordinal), ThreadUpdates = args.Contains("thread", StringComparer.Ordinal) };
        WebApplication? host = null;
        ServiceProvider? provider = null;
        BlazorWebView? hybrid = null;
        WebView2CompositionControl? browser = null;
        var profile = Path.Combine(Path.GetTempPath(), "ChatStageOverlayProbe");
        try
        {
            if (server)
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Development" });
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                Configure(builder.Services, state);
                builder.Services.AddRazorComponents().AddInteractiveServerComponents();
                host = builder.Build();
                host.UseAntiforgery();
                host.MapStaticAssets();
                host.MapRazorComponents<ProbeApp>().AddInteractiveServerRenderMode();
                await host.StartAsync();
                browser = new WebView2CompositionControl { CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = profile } };
                window.Content = browser;
                await browser.EnsureCoreWebView2Async();
                var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                browser.Source = new Uri(address);
            }
            else
            {
                var services = new ServiceCollection();
                services.AddWpfBlazorWebView();
                Configure(services, state);
                provider = services.BuildServiceProvider();
                hybrid = new BlazorWebView { UseCompositionControl = true, HostPage = "wwwroot/index.html", Services = provider };
                hybrid.BlazorWebViewInitializing += (_, e) => e.UserDataFolder = profile;
                hybrid.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(ProbeChat) });
                window.Content = hybrid;
            }

            var session = await state.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(1000);
            using var process = Process.GetCurrentProcess();
            var core = hybrid?.WebView.CoreWebView2 ?? browser!.CoreWebView2;
            var browserCpu = core.Environment.GetProcessInfos().ToDictionary(p => p.ProcessId, p => ReadProcess(p.ProcessId).Cpu);
            var cpu = process.TotalProcessorTime;
            var rootRenders = state.RootRenders;
            var timer = Stopwatch.StartNew();
            await session.SubmitAsync("Stream five seconds of text");
            timer.Stop();
            process.Refresh();
            var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            var webCpuMs = 0.0;
            var webPrivateBytes = 0L;
            foreach (var info in core.Environment.GetProcessInfos())
            {
                var current = ReadProcess(info.ProcessId);
                webCpuMs += (current.Cpu - browserCpu.GetValueOrDefault(info.ProcessId)).TotalMilliseconds;
                webPrivateBytes += current.PrivateBytes;
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"RESULT mode={(server ? "server" : "hybrid")} root={(state.Before ? "before" : "after")} updates={(state.ThreadUpdates ? "thread" : "text")} renders={state.RootRenders - rootRenders} deltas={state.Deltas} elapsedMs={timer.Elapsed.TotalMilliseconds:F0} cpuMs={cpuMs:F0} cpuOneCorePercent={cpuMs / timer.Elapsed.TotalMilliseconds * 100:F2} privateMiB={process.PrivateMemorySize64 / 1048576.0:F1} webCpuMs={webCpuMs:F0} webPrivateMiB={webPrivateBytes / 1048576.0:F1} totalCpuOneCorePercent={(cpuMs + webCpuMs) / timer.Elapsed.TotalMilliseconds * 100:F2} totalPrivateMiB={(process.PrivateMemorySize64 + webPrivateBytes) / 1048576.0:F1} control={(hybrid?.WebView.GetType().Name ?? browser!.GetType().Name)}"));
        }
        finally
        {
            if (hybrid is not null) await hybrid.DisposeAsync();
            browser?.Dispose();
            if (host is not null) await host.DisposeAsync();
            if (provider is not null) await provider.DisposeAsync();
        }
    }

    private static (TimeSpan Cpu, long PrivateBytes) ReadProcess(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return (process.TotalProcessorTime, process.PrivateMemorySize64);
        }
        catch (ArgumentException) { return (TimeSpan.Zero, 0); }
        catch (InvalidOperationException) { return (TimeSpan.Zero, 0); }
    }
}

