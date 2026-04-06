using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace NetworkMonitor;

static class DashboardSessionAuth
{
    public const string SessionCookieName = "networkmonitor-dashboard-session";
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Sessions = new(StringComparer.Ordinal);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private const string PasswordHashPrefix = "NM1$PBKDF2$SHA256$";

    public static bool ValidateCredentials(AppConfig config, string username, string password)
    {
        if (!config.DashboardAuthEnabled)
            return true;

        if (string.IsNullOrWhiteSpace(config.DashboardAuthUsername) || string.IsNullOrWhiteSpace(config.DashboardAuthPasswordHash))
            return false;

        var isUsernameValid = FixedTimeEquals(username, config.DashboardAuthUsername);
        var isPasswordValid = VerifyPasswordHash(password, config.DashboardAuthPasswordHash);

        return isUsernameValid & isPasswordValid;
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

    public static void SignIn(HttpRequest request, HttpResponse response)
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
            Secure = UseSecureCookies(request),
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
            Secure = UseSecureCookies(request),
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

    private static bool VerifyPasswordHash(string password, string storedHash)
    {
        if (!storedHash.StartsWith(PasswordHashPrefix, StringComparison.Ordinal))
            return false;

        var parts = storedHash.Split('$', StringSplitOptions.TrimEntries);
        if (parts.Length != 6 || !int.TryParse(parts[3], out var iterations) || iterations <= 0)
            return false;

        try
        {
            var salt = Convert.FromBase64String(parts[4]);
            var expectedHash = Convert.FromBase64String(parts[5]);
            var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool UseSecureCookies(HttpRequest request)
    {
        if (request.IsHttps)
            return true;

        if (!request.Headers.TryGetValue("X-Forwarded-Proto", out StringValues forwardedProto))
            return false;

        return forwardedProto.Any(value => string.Equals(value, "https", StringComparison.OrdinalIgnoreCase));
    }
}
