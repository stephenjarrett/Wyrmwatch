using System.Net;
using System.Net.Http.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Wyrmwatch.Core;
using Wyrmwatch.Desktop;

namespace Wyrmwatch.Desktop.Tests;

public class GameSettingsFlowsTests : IsolatedDesktopTest
{
    [AvaloniaFact]
    public async Task UnreadableLocalPasswordUsesAuthenticatedAgentWithoutWritingGameConfig()
    {
        var profile = new ServerProfile { InstallPath = Path.Combine(Program.DataDirectory, "server"), BackupPath = Path.Combine(Program.DataDirectory, "backups") };
        var store = new JsonStore(Program.DataDirectory);
        store.Write("settings.json", new ManagerSettings { Servers = [profile] });
        store.Write("server-admin-passwords.json", new Dictionary<string, string> { [profile.Id] = "unreadable!" });
        AtomicFile.Write(profile.ConfigPath, "[/Script/Dominion.DedicatedServerSettings]\nOwnerId=dummy-owner\nServerName=Dummy\nDefaultWorldName=Dummy\nPort=7777\n");
        var original = File.ReadAllText(profile.ConfigPath);
        var handler = new ConfigurationHandler();
        using var client = new AgentClient(Program.DataDirectory, new HttpClient(handler) { BaseAddress = new Uri("http://fixture/") });
        var window = new MainWindow(client); window.Show();
        try
        {
            var input = window.GetLogicalDescendants().OfType<TextBox>().Single(c => c.Name == "AdminPassword");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (input.Text != "dummy-service-private") { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, deadline.Token); }
            Assert.Equal(1, handler.Reads); Assert.Equal(original, File.ReadAllText(profile.ConfigPath));
        }
        finally { window.Close(); }
    }
    private sealed class ConfigurationHandler : HttpMessageHandler
    {
        public int Reads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.EndsWith("/configuration", request.RequestUri!.AbsolutePath); Assert.Equal(HttpMethod.Get, request.Method);
            Reads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Dictionary<string, string> { ["AdminPassword"] = "dummy-service-private" }) });
        }
    }
}
