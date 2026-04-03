using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace NetworkMonitor;

static class DashboardWebServer
{
    public static async Task<WebApplication> StartAsync(Func<DashboardSnapshot> snapshotFactory, ManualCheckTrigger manualCheckTrigger, ILogger logger, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
            builder.WebHost.UseUrls("http://0.0.0.0:8080");

        var app = builder.Build();

        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(webRoot))
        {
            app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = new PhysicalFileProvider(webRoot)
            });

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(webRoot)
            });
        }

        app.MapGet("/api/dashboard", () =>
        {
            var json = JsonSerializer.Serialize(snapshotFactory(), DashboardJsonContext.Default.DashboardSnapshot);
            return Results.Text(json, "application/json");
        });

        app.MapPost("/api/actions/check-now", () =>
        {
            manualCheckTrigger.Request();
            var response = new DashboardActionResponse
            {
                Success = true,
                Message = "Un cycle de vérification immédiat a été demandé."
            };

            var json = JsonSerializer.Serialize(response, typeof(DashboardActionResponse), DashboardJsonContext.Default);
            return Results.Text(json, "application/json");
        });

        app.MapPost("/api/actions/clear-snooze", (string key) =>
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                var invalid = new DashboardActionResponse
                {
                    Success = false,
                    Message = "La clé du moniteur est obligatoire."
                };

                var invalidJson = JsonSerializer.Serialize(invalid, typeof(DashboardActionResponse), DashboardJsonContext.Default);
                return Results.Text(invalidJson, "application/json", statusCode: StatusCodes.Status400BadRequest);
            }

            var hadSnooze = PushoverSnooze.IsSnoozed(key);
            PushoverSnooze.ClearSnooze(key);

            var response = new DashboardActionResponse
            {
                Success = true,
                Message = hadSnooze
                    ? $"Le snooze du moniteur '{key}' a été supprimé."
                    : $"Aucun snooze actif n'était présent pour '{key}'."
            };

            var json = JsonSerializer.Serialize(response, typeof(DashboardActionResponse), DashboardJsonContext.Default);
            return Results.Text(json, "application/json");
        });

        app.MapGet("/api/health", () => Results.Text("{\"status\":\"ok\"}", "application/json"));

        app.MapFallback(async context =>
        {
            var indexPath = Path.Combine(webRoot, "index.html");
            if (!File.Exists(indexPath))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(indexPath, context.RequestAborted);
        });

        await app.StartAsync(ct);
        logger.LogInformation("Tableau de bord web disponible sur {Urls}", string.Join(", ", app.Urls));
        return app;
    }
}
