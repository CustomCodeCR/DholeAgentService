using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace Dhole.Agent.IntegrationTests;

[TestClass]
public sealed class StandbyViewerGatewayOriginTests
{
    private static bool AllowedOrigin(string origin)
    {
        var gatewayType = Assembly.Load("Dhole.Agent.Api").GetType(
            "Dhole.Agent.Api.Endpoints.StandbyViewerGateway", throwOnError: true)!;
        var method = gatewayType.GetMethod("AllowedOrigin",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var context = new DefaultHttpContext();
        // The API Gateway forwards the public Origin while Request.Host
        // changes to the internal destination. It must not reject the real
        // noVNC page simply because the upstream Host differs.
        context.Request.Host = new HostString("dhole-dhole-agent-api-1", 8080);
        context.Request.Headers.Origin = origin;
        return (bool)method.Invoke(null, [context])!;
    }

    [DataTestMethod]
    [DataRow("https://api.logisticacastrofallas.com")]
    [DataRow("https://dhole-api.customcodecr.com")]
    [DataRow("https://sistema.logisticacastrofallas.com")]
    [DataRow("https://dhole.customcodecr.com")]
    public void AllowsAuthenticatedViewerAndTrustedSpaOrigins(string origin)
        => Assert.IsTrue(AllowedOrigin(origin));

    [DataTestMethod]
    [DataRow("https://api.logisticacastrofallas.com.evil.example")]
    [DataRow("http://api.logisticacastrofallas.com")]
    [DataRow("https://evil.example")]
    [DataRow("null")]
    public void RejectsUntrustedWebSocketOrigins(string origin)
        => Assert.IsFalse(AllowedOrigin(origin));
}
