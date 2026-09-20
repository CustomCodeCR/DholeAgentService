using System.Text.RegularExpressions;

namespace Dhole.Agent.Infrastructure.Secrets;

public sealed partial class SecretRedactor
{
    private static readonly string[] SensitiveKeys =
    [
        "authorization",
        "bearer",
        "password",
        "passwd",
        "cookie",
        "set-cookie",
        "refresh_token",
        "refreshToken",
        "access_token",
        "accessToken",
        "jwt",
        "akamai",
        "bm-telemetry"
    ];

    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var redacted = BearerRegex().Replace(value, "$1[REDACTED]");

        foreach (var key in SensitiveKeys)
        {
            redacted = Regex.Replace(
                redacted,
                $@"(?i)([""']?{Regex.Escape(key)}[""']?\s*[:=]\s*)([""'][^""']*[""']|[^\s,;}}]+)",
                "$1[REDACTED]");
        }

        return redacted;
    }

    [GeneratedRegex(@"(?i)(Bearer\s+)[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex BearerRegex();
}
