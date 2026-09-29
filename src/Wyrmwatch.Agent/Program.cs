using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Dragonwilds.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using Wyrmwatch.Agent;

var workspaceIndex = Array.IndexOf(args, "--workspace");
var workspace = workspaceIndex >= 0 && workspaceIndex + 1 < args.Length ? Path.GetFullPath(args[workspaceIndex + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wyrmwatch");
using var lease = WorkspaceLease.Acquire(workspace);
WorkspaceLease.Protect(workspace);
var store = new JsonStore(workspace);
var manager = new ManagerHost(store);
var parentIndex = Array.IndexOf(args, "--parent");
manager.PersistentHost = parentIndex < 0;
if (parentIndex >= 0 && parentIndex + 2 < args.Length) manager.Attach(new(int.Parse(args[parentIndex + 1]), new DateTime(long.Parse(args[parentIndex + 2]), DateTimeKind.Utc), ""));
var remote = store.Read("remote.json", () => new RemoteSettings());
var secret = AccessPolicy.NewSecret();
var secretHash = AccessPolicy.Hash(secret);
X509Certificate2? certificate = null;
string? remoteWarning = null;
WebApplication CreateHost(bool allowRemote)
{
    var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
    builder.Logging.ClearProviders();
    builder.Services.AddWindowsService(options => options.ServiceName = "Wyrmwatch");
    builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMinutes(50));
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    });
    if (remote.Enabled && allowRemote)
    {
        try
        {
            if (remote.Port is < 1024 or > 65535) throw new ArgumentException("Remote access port must be between 1024 and 65535.");
            var path = Path.Combine(workspace, "remote-certificate.pfx"); SafePaths.NoLinks(path);
            if (!File.Exists(path))
            {
                using var key = RSA.Create(3072);
                var request = new CertificateRequest("CN=Wyrmwatch", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddDnsName(Environment.MachineName); names.AddIpAddress(IPAddress.Loopback);
                foreach (var address in Dns.GetHostAddresses(Environment.MachineName).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) names.AddIpAddress(address);
                request.CertificateExtensions.Add(names.Build());
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
                File.WriteAllBytes(path, generated.Export(X509ContentType.Pfx)); WorkspaceLease.Protect(path);
                File.WriteAllBytes(Path.Combine(workspace, "remote-certificate.cer"), generated.Export(X509ContentType.Cert));
            }
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null);
            if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow) throw new CryptographicException("The remote certificate is invalid or expired. Replace it before enabling remote access.");
            manager.RemoteAddress = $"https://{Environment.MachineName}:{remote.Port}";
        }
        catch (Exception error) when (error is IOException or CryptographicException or ArgumentException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
        {
            remoteWarning = "Remote access is unavailable: " + error.Message; manager.WriteLog(remoteWarning);
            certificate?.Dispose(); certificate = null;
        }
    }
    builder.WebHost.ConfigureKestrel(server =>
    {
        server.Limits.MaxRequestBodySize = 64 * 1024;
        server.Listen(IPAddress.Loopback, 0);
        if (remote.Enabled && allowRemote && certificate is not null) server.ListenAnyIP(remote.Port, listener => listener.UseHttps(certificate));
    });
    var app = builder.Build();
    app.UseRateLimiter();
    app.Use(async (context, next) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}") { context.Response.StatusCode = 403; return; }
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/admin"))
        {
            var header = context.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
            var local = !context.Request.IsHttps && IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None) && AccessPolicy.Matches(token, secretHash);
            var grant = local ? null : manager.Grants().FirstOrDefault(g => g.Expires > DateTimeOffset.UtcNow && AccessPolicy.Matches(token, g.SecretHash));
            if (!local && grant is null) { context.Response.StatusCode = 401; return; }
            if (context.Request.Path.StartsWithSegments("/admin") && !local) { context.Response.StatusCode = 403; return; }
            context.Items["local"] = local; context.Items["grant"] = grant;
        }
        try { await next(context); }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            context.Response.StatusCode = error is KeyNotFoundException ? 404 : error is InvalidOperationException ? 409 : 400;
            await context.Response.WriteAsJsonAsync(new ActionResult(error.Message));
        }
    });
    bool Allowed(HttpContext context, string id, string action) => context.Items["local"] is true || context.Items["grant"] is AccessGrant grant && AccessPolicy.Allows(grant, id, action, DateTimeOffset.UtcNow);
    app.MapGet("/api/status", (HttpContext context) =>
    {
        var status = manager.Status();
        if (context.Items["local"] is true) return Results.Ok(status);
        var servers = status.Servers.Where(s => Allowed(context, s.Id, "view")).Select(s => s with { State = s.State with { Processes = [] }, Actions = s.Actions.Where(a => Allowed(context, s.Id, a)).ToArray() }).ToList();
        return Results.Ok(status with { Servers = servers, Operations = status.Operations.Where(o => servers.Any(s => s.Id == o.ServerId)).Select(o => o with { Detail = o.Status == "Failed" ? "Operation failed. Ask the host to inspect the local activity log." : o.Action + ": " + o.Status }).ToList(), Log = [] });
    });
    app.MapGet("/api/servers/{id}/backups", (HttpContext context, string id) => Allowed(context, id, "backups") ? Results.Ok(manager.BackupList(id)) : Results.StatusCode(403));
    app.MapPost("/api/servers/{id}/actions", async (HttpContext context, string id, ServerAction action) =>
    {
        if (!Allowed(context, id, action.Action)) return Results.StatusCode(403);
        return Results.Ok(new ActionResult(await manager.ExecuteAsync(id, action)));
    });
    app.MapGet("/admin/settings", () => manager.Settings);
    app.MapGet("/admin/servers/{id}/log", (string id) => new ActionResult(LogTail.Read(manager.Profile(id).LogPath)));
    app.MapPut("/admin/preferences", async (ManagerSettings settings) => { await manager.SavePreferencesAsync(settings); return Results.Ok(); });
    app.MapPut("/admin/profiles", async (ServerProfile profile) => Results.Ok(await manager.SaveProfileAsync(profile)));
    app.MapDelete("/admin/profiles/{id}", async (string id) => { await manager.RemoveProfileAsync(id); return Results.Ok(); });
    app.MapPost("/admin/attach", (ProcessIdentity identity) => { manager.Attach(identity); return Results.Ok(); });
    app.MapGet("/admin/remote", () => new RemoteInfo(remote, manager.RemoteAddress, certificate?.GetCertHashString(HashAlgorithmName.SHA256), remoteWarning));
    app.MapPut("/admin/remote", (RemoteSettings settings) =>
    {
        if (manager.Busy) return Results.Conflict(new ActionResult("Wait for the current operation before changing remote access."));
        if (settings.Port is < 1024 or > 65535) return Results.BadRequest(new ActionResult("Choose a port from 1024 to 65535."));
        store.Write("remote.json", settings); return Results.Ok(new ActionResult("Remote settings saved. Restart the background manager to apply them."));
    });
    app.MapGet("/admin/access", () => manager.Grants().Select(g => new { g.Id, g.Name, g.Role, g.ServerId, g.Expires }));
    app.MapPost("/admin/access", (CreateAccessGrant request) => manager.Issue(request));
    app.MapDelete("/admin/access/{id}", (string id) => { manager.Revoke(id); return Results.Ok(); });
    app.MapPost("/admin/shutdown", () =>
    {
        if (!manager.TryPrepareShutdown()) return Results.Conflict(new ActionResult("An operation is still running. The background manager remains active."));
        _ = Task.Run(async () => { await Task.Delay(300); app.Lifetime.StopApplication(); });
        return Results.Ok();
    });
    app.MapGet("/", () => Results.File(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"), "text/html"));
    app.MapGet("/app.js", () => Results.File(Path.Combine(AppContext.BaseDirectory, "wwwroot", "app.js"), "text/javascript"));
    app.MapGet("/app.css", () => Results.File(Path.Combine(AppContext.BaseDirectory, "wwwroot", "app.css"), "text/css"));
    app.MapGet("/license", () => DistributionFile("LICENSE.txt", "text/plain"));
    app.MapGet("/source", () => DistributionFile("Wyrmwatch-source.zip", "application/zip"));
    return app;
}
IResult DistributionFile(string name, string type)
{
    var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", name));
    return File.Exists(path) ? Results.File(path, type, name) : Results.NotFound(new ActionResult("This development build has no bundled distribution files. Build a portable package to serve its matching license and source archive."));
}
var app = CreateHost(true);
try { await app.StartAsync(); }
catch (Exception error) when (manager.RemoteAddress is not null && error is IOException or System.Net.Sockets.SocketException)
{
    await app.DisposeAsync(); certificate?.Dispose(); certificate = null; manager.RemoteAddress = null;
    remoteWarning = "Remote access could not listen on the selected port. Local management remains available. " + error.Message; manager.WriteLog(remoteWarning);
    app = CreateHost(false); await app.StartAsync();
}
var localAddress = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single(a => a.StartsWith("http://", StringComparison.Ordinal));
using var process = Process.GetCurrentProcess();
store.Write("agent.json", new AgentEndpoint(process.Id, process.StartTime.ToUniversalTime().Ticks, localAddress, secret, ManagerHost.Version));
WorkspaceLease.Protect(Path.Combine(workspace, "agent.json"));
var monitor = manager.MonitorAsync(app.Lifetime.StopApplication, app.Lifetime.ApplicationStopping);
await app.WaitForShutdownAsync();
await monitor;
certificate?.Dispose();
