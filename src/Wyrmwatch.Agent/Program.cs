using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Wyrmwatch.Core;
using Wyrmwatch.Agent;

var workspaceIndex = Array.IndexOf(args, "--workspace");
var workspace = workspaceIndex >= 0 && workspaceIndex + 1 < args.Length ? Path.GetFullPath(args[workspaceIndex + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wyrmwatch");
FileStream acquiredLease;
try { acquiredLease = WorkspaceLease.Acquire(workspace); }
catch (IOException error) when (error.Message == WorkspaceLease.OwnershipFailureMessage)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
using var lease = acquiredLease;
WorkspaceLease.Protect(workspace);
var store = new JsonStore(workspace);
var manager = new ManagerHost(store);
var parentIndex = Array.IndexOf(args, "--parent");
manager.PersistentHost = parentIndex < 0;
if (parentIndex >= 0 && parentIndex + 2 < args.Length) manager.Attach(new(int.Parse(args[parentIndex + 1]), args[parentIndex + 2]));
var secret = AccessPolicy.NewSecret();
await using var app = AgentServer.Create(manager, secret);
await app.StartAsync();
var localAddress = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var process = Process.GetCurrentProcess();
store.Write("agent.json", new AgentEndpoint(process.Id, ProcessLifetime.Token(process), localAddress, secret, ManagerHost.Version));
WorkspaceLease.Protect(Path.Combine(workspace, "agent.json"));
var monitor = manager.MonitorAsync(app.Lifetime.StopApplication, app.Lifetime.ApplicationStopping);
await app.WaitForShutdownAsync();
await monitor;
return 0;
