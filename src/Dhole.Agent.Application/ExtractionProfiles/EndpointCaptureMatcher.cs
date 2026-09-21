using System.Text.RegularExpressions;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.ExtractionProfiles;

public sealed class EndpointCaptureMatcher
{
    public bool IsMatch(AgentEndpointCapture rule, string httpMethod, string url, string? contentType = null)
    {
        if (!rule.IsActive || rule.IsDeleted)
            return false;

        if (!string.Equals(rule.HttpMethod, httpMethod?.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(rule.ContentType) &&
            (string.IsNullOrWhiteSpace(contentType) || !contentType.Contains(rule.ContentType, StringComparison.OrdinalIgnoreCase)))
            return false;

        return rule.MatchType switch
        {
            AgentEndpointMatchType.Contains => url.Contains(rule.UrlPattern, StringComparison.OrdinalIgnoreCase),
            AgentEndpointMatchType.Exact => string.Equals(url, rule.UrlPattern, StringComparison.OrdinalIgnoreCase),
            AgentEndpointMatchType.Regex => Regex.IsMatch(url, rule.UrlPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)),
            _ => false
        };
    }

    public void ValidatePattern(AgentEndpointMatchType matchType, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("URL pattern is required.", nameof(pattern));

        if (matchType == AgentEndpointMatchType.Regex)
            _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }
}
