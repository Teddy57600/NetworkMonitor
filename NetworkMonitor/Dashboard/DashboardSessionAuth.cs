using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace NetworkMonitor;

static class DashboardSessionAuth
{
    public const string SessionCookieName = "networkmonitor-dashboard-session";
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Sessions = new(StringComparer.Ordinal);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    public static bool ValidateCredentials(AppConfig config, string username, string password)
    {
        if (!config.DashboardAuthEnabled)
            return true;

        if (string.IsNullOrWhiteSpace(config.DashboardAuthUsername) || string.IsNullOrWhiteSpace(config.DashboardAuthPassword))
            return false;

        return FixedTimeEquals(username, config.DashboardAuthUsername)
            && FixedTimeEquals(password, config.DashboardAuthPassword);
    }

    public static bool IsAuthenticated(HttpRequest request)
    {
        CleanupExpiredSessions();

        if (!request.Cookies.TryGetValue(SessionCookieName, out var token) || string.IsNullOrWhiteSpace(token))
            return false;

        if (Sessions.TryGetValue(token, out var expiresAt) && expiresAt > DateTimeOffset.UtcNow)
            return true;

        Sessions.TryRemove(token, out _);
        return false;
    }

    public static void SignIn(HttpResponse response)
    {
        CleanupExpiredSessions();

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime);
        Sessions[token] = expiresAt;

        response.Cookies.Append(SessionCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = false,
            MaxAge = SessionLifetime,
            Path = "/"
        });
    }

    public static void SignOut(HttpRequest request, HttpResponse response)
    {
        if (request.Cookies.TryGetValue(SessionCookieName, out var token) && !string.IsNullOrWhiteSpace(token))
            Sessions.TryRemove(token, out _);

        response.Cookies.Delete(SessionCookieName, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = false,
            Path = "/"
        });
    }

    private static void CleanupExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var session in Sessions)
        {
            if (session.Value <= now)
                Sessions.TryRemove(session.Key, out _);
        }
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
