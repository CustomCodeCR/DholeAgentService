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

        var redacted = value;

        foreach (var key in SensitiveKeys)
        {
            redacted = Regex.Replace(
                redacted,
                $@"(?i)([""']?{Regex.Escape(key)}[""']?\s*[:=]\s*)([""'][^""']*[""']|[^,;\r\n}}]+)",
                "$1[REDACTED]");
        }

        return BearerRegex().Replace(redacted, "$1[REDACTED]");
    }

    [GeneratedRegex(@"(?i)(Bearer\s+)[A-Za-z0-9\-._~+/]+=*")]
    private static partial Regex BearerRegex();
}
