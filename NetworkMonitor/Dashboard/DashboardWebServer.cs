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

        app.MapGet("/login", async context =>
        {
            var currentConfig = AppConfigProvider.Current;
            if (!currentConfig.DashboardAuthEnabled || DashboardSessionAuth.IsAuthenticated(context.Request))
            {
                context.Response.Redirect("/");
                return;
            }

            var loginPath = Path.Combine(webRoot, "login.html");
            if (!File.Exists(loginPath))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            var html = await File.ReadAllTextAsync(loginPath, context.RequestAborted);
            html = html
                .Replace("{{APP_VERSION}}", currentConfig.AppVersion)
                .Replace("{{TIMEZONE}}", TimeZoneInfo.Local.Id);

            await context.Response.WriteAsync(html, context.RequestAborted);
        });

        app.MapPost("/login", async context =>
        {
            var currentConfig = AppConfigProvider.Current;
            if (!currentConfig.DashboardAuthEnabled)
            {
                context.Response.Redirect("/");
                return;
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var username = form["username"].ToString();
            var password = form["password"].ToString();

            if (!DashboardSessionAuth.ValidateCredentials(currentConfig, username, password))
            {
                context.Response.Redirect("/login?error=1");
                return;
            }

            DashboardSessionAuth.SignIn(context.Response);
            context.Response.Redirect("/");
        });

        app.MapPost("/logout", context =>
        {
            DashboardSessionAuth.SignOut(context.Request, context.Response);
            context.Response.Redirect("/login");
            return Task.CompletedTask;
        });

        app.Use(async (context, next) =>
        {
            var currentConfig = AppConfigProvider.Current;
            if (!currentConfig.DashboardAuthEnabled)
            {
                await next(context);
                return;
            }

            if (context.Request.Path.StartsWithSegments("/login", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            if (DashboardSessionAuth.IsAuthenticated(context.Request))
            {
                await next(context);
                return;
            }

            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                var payload = JsonSerializer.Serialize(new DashboardActionResponse
                {
                    Success = false,
                    Message = "Authentification requise."
                }, typeof(DashboardActionResponse), DashboardJsonContext.Default);

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(payload, context.RequestAborted);
                return;
            }

            context.Response.Redirect("/login");
        });

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

        app.MapPost("/api/actions/snooze", (string key, int? durationMinutes) =>
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

            var config = AppConfigProvider.Current;
            if (durationMinutes is <= 0 or > 525600)
            {
                var invalidDuration = new DashboardActionResponse
                {
                    Success = false,
                    Message = "La durée du snooze doit être comprise entre 1 minute et 365 jours."
                };

                var invalidDurationJson = JsonSerializer.Serialize(invalidDuration, typeof(DashboardActionResponse), DashboardJsonContext.Default);
                return Results.Text(invalidDurationJson, "application/json", statusCode: StatusCodes.Status400BadRequest);
            }

            var duration = durationMinutes.HasValue
                ? TimeSpan.FromMinutes(durationMinutes.Value)
                : TimeSpan.FromDays(config.SnoozeDays);

            var snoozeUntil = PushoverSnooze.SetSnooze(key, duration);
            logger.LogInformation("🔕 Snooze manuel activé pour {Key} pendant {Duration} jusqu'au {Until:dd/MM/yyyy HH:mm} UTC", key, duration, snoozeUntil);

            var response = new DashboardActionResponse
            {
                Success = true,
                Message = $"Le moniteur '{key}' est snoozé jusqu'au {snoozeUntil:dd/MM/yyyy HH:mm} UTC."
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

        app.MapPost("/api/actions/add-ping", (string target) =>
        {
            var result = AppConfigProvider.AddPingTarget(target);
            var response = new DashboardActionResponse
            {
                Success = result.Success,
                Message = result.Message
            };

            var json = JsonSerializer.Serialize(response, typeof(DashboardActionResponse), DashboardJsonContext.Default);
            return Results.Text(
                json,
                "application/json",
                statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        });

        app.MapPost("/api/actions/remove-ping", (string target) =>
        {
            var result = AppConfigProvider.RemovePingTarget(target);
            var response = new DashboardActionResponse
            {
                Success = result.Success,
                Message = result.Message
            };

            var json = JsonSerializer.Serialize(response, typeof(DashboardActionResponse), DashboardJsonContext.Default);
            return Results.Text(
                json,
                "application/json",
                statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        });

        app.MapPost("/api/actions/add-tcp", (string host, int port) =>
        {
            var result = AppConfigProvider.AddTcpTarget(host, port);
            var response = new DashboardActionResponse
            {
                Success = result.Success,
                Message = result.Message
            };

            var json = JsonSerializer.Serialize(response, typeof(DashboardActionResponse), DashboardJsonContext.Default);
            return Results.Text(
                json,
                "application/json",
                statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        });

        app.MapPost("/api/actions/remove-tcp", (string host, int port) =>
        {
            var result = AppConfigProvider.RemoveTcpTarget(host, port);
            var response = new DashboardActionResponse
            {
                Success = result.Success,
                Message = result.Message
            };

            var json = JsonSerializer.Serialize(response, typeof(DashboardActionResponse), DashboardJsonContext.Default);
            return Results.Text(
                json,
                "application/json",
                statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        });

        app.MapGet("/api/health", () => Results.Text("{\"status\":\"ok\"}", "application/json"));

        app.MapFallback(async context =>
        {
            if (AppConfigProvider.Current.DashboardAuthEnabled && !DashboardSessionAuth.IsAuthenticated(context.Request))
            {
                context.Response.Redirect("/login");
                return;
            }

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
