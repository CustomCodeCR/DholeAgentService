namespace Dhole.Agent.Infrastructure.Providers.Maersk.Authentication;

/// <summary>
/// Positive browser-session evidence from the official Maersk booking header.
/// A visible user avatar with matching name is accepted; mere navigation to
/// /book, prices, and public header links are never sufficient.
/// </summary>
public static class MaerskBookingAccountEvidence
{
    public static bool IsOfficialBookingPage(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && (uri.IdnHost.Equals("www.maersk.com", StringComparison.OrdinalIgnoreCase)
               || uri.IdnHost.Equals("maersk.com", StringComparison.OrdinalIgnoreCase))
           && (uri.AbsolutePath.Equals("/book", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.StartsWith("/book/", StringComparison.OrdinalIgnoreCase));

    public static bool AvatarMatchesDisplayName(string? initials, string? name)
    {
        if (string.IsNullOrWhiteSpace(initials) || string.IsNullOrWhiteSpace(name)
            || initials.Length != 2 || !initials.All(c => char.IsLetter(c) && char.IsUpper(c)))
            return false;

        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 5
            || parts.Any(part => part.Length < 2 || !char.IsLetter(part[0])))
            return false;

        // The avatar is generated from the user's given and family names.
        // Reject generic navigation labels, where two words could coincide.
        var joined = string.Join(" ", parts);
        if (joined.Equals("Contact Us", StringComparison.OrdinalIgnoreCase)
            || joined.Equals("Ask Maersk", StringComparison.OrdinalIgnoreCase)
            || joined.Equals("Maersk Spot", StringComparison.OrdinalIgnoreCase)
            || joined.Equals("Sign In", StringComparison.OrdinalIgnoreCase)
            || joined.Equals("Log In", StringComparison.OrdinalIgnoreCase))
            return false;

        return char.ToUpperInvariant(parts[0][0]) == initials[0]
               && char.ToUpperInvariant(parts[1][0]) == initials[1]
               && parts.Take(2).All(part => char.IsUpper(part[0])
                   && part.Skip(1).Any(char.IsLower));
    }
}
