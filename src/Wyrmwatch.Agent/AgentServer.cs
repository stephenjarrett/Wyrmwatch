using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Wyrmwatch.Core;

namespace Wyrmwatch.Agent;

// The desktop and tests use the same authenticated, loopback-only HTTP surface.
public static class AgentServer
{
    public static WebApplication Create(ManagerHost manager, string secret)
    {
        var secretHash = AccessPolicy.Hash(secret);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Services.AddWindowsService(options => options.ServiceName = "Wyrmwatch");
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMinutes(50));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        builder.WebHost.ConfigureKestrel(server => { server.Limits.MaxRequestBodySize = 64 * 1024; server.Listen(IPAddress.Loopback, 0); });
        var app = builder.Build();
        app.UseRateLimiter();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}") { context.Response.StatusCode = 403; return; }
            var header = context.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
            if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None) || !AccessPolicy.Matches(token, secretHash)) { context.Response.StatusCode = 401; return; }
            try { await next(context); }
            catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException)
            {
                context.Response.StatusCode = error is KeyNotFoundException ? 404 : error is InvalidOperationException ? 409 : 400;
                await context.Response.WriteAsJsonAsync(new ActionResult(error.Message));
            }
        });
        app.MapGet("/api/status", () => manager.Status());
        app.MapGet("/api/servers/{id}/backups", (string id) => manager.BackupList(id));
        app.MapPost("/api/servers/{id}/actions", async (string id, ServerAction action) => new ActionResult(await manager.ExecuteAsync(id, action)));
        app.MapGet("/admin/settings", () => manager.Settings);
        app.MapGet("/admin/servers/{id}/log", (string id) => new ActionResult(LogTail.Read(manager.Profile(id).LogPath)));
        app.MapPut("/admin/preferences", async (ManagerSettings settings) => { await manager.SavePreferencesAsync(settings); return Results.Ok(); });
        app.MapPut("/admin/profiles", async (ServerProfile profile) => await manager.SaveProfileAsync(profile));
        app.MapPost("/admin/import", async (ServerProfile profile) => await manager.ImportProfileAsync(profile));
        app.MapPost("/admin/create", async (CreateServerRequest request) => await manager.CreateServerAsync(request));
        app.MapDelete("/admin/profiles/{id}", async (string id) => { await manager.RemoveProfileAsync(id); return Results.Ok(); });
        app.MapPost("/admin/attach", (AgentParent identity) => { manager.Attach(identity); return Results.Ok(); });
        app.MapPost("/admin/shutdown", () =>
        {
            if (!manager.TryPrepareShutdown()) return Results.Conflict(new ActionResult("An operation is still running. The background manager remains active."));
            _ = Task.Run(async () => { await Task.Delay(300); app.Lifetime.StopApplication(); });
            return Results.Ok();
        });
        return app;
    }
}
