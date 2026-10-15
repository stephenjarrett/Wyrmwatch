using System.Net;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Wyrmwatch.Core;
using Wyrmwatch.Desktop;
using DesktopProgram = Wyrmwatch.Desktop.Program;

// CI-only native driver. It calls the real check/download handlers and launches
// the real installer; it never supplies an alternate restart implementation.
internal static class FixtureProgram
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("CI") != "true" || args.Length != 2) return 2;
        var workspace = Path.GetFullPath(args[0]);
        var fixture = Path.GetFullPath(args[1]);
        if (!Path.GetFileName(Path.GetDirectoryName(workspace)!).StartsWith("wyrmwatch-update-fixture-", StringComparison.Ordinal)) return 2;
        DesktopProgram.DataDirectory = workspace;
        DesktopProgram.BuildAvaloniaApp().AfterSetup(_ => Dispatcher.UIThread.Post(async () =>
        {
            var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            try
            {
                var window = (MainWindow)lifetime.MainWindow!;
                if (window.FindControl<TextBox>("AdminPassword")?.Text != "disposable-fixture-only")
                    throw new InvalidOperationException("The native fixture did not display its retained synthetic credential.");
                window.AppUpdateClientFactory = () => new AppUpdates(() => new HttpClient(new FixtureHttp(fixture)), installedWindows: true);
                await window.CheckManagerUpdateAsync();
                await window.DownloadManagerUpdateAsync();
                // A successful handoff shuts down the application itself. If it
                // returns with the window alive, report the real UI state.
                var model = (WorkspaceModel)window.DataContext!;
                File.WriteAllBytes(Path.Combine(fixture, "driver-result.json"), JsonSerializer.SerializeToUtf8Bytes(new
                {
                    model.HasError, model.ErrorSummary,
                    Status = window.FindControl<TextBlock>("AppUpdateStatus")?.Text,
                    RetryVisible = window.FindControl<Button>("SwitchAppButton")?.IsVisible
                }));
                if (lifetime.MainWindow?.IsVisible == true) window.Close();
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(fixture, "driver-failure.txt"), error.GetType().Name);
                lifetime.Shutdown(1);
            }
        }, DispatcherPriority.Background)).StartWithClassicDesktopLifetime([]);
        return Environment.ExitCode;
    }

    private sealed class FixtureHttp(string directory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            var name = uri.Host == "api.github.com" && uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)
                ? "release.json" : Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
            if (uri.Host is not ("api.github.com" or "github.com") || name.Contains('/') || name.Contains('\\'))
                throw new IOException("Unexpected native fixture request.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(File.OpenRead(Path.Combine(directory, name)))
            });
        }
    }
}
