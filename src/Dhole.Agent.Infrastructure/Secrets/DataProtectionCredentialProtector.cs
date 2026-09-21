using Dhole.Agent.Application.Abstractions.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Dhole.Agent.Infrastructure.Secrets;

public sealed class DataProtectionCredentialProtector : ICredentialProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionCredentialProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Dhole.Agent.Credentials.v1");
    }

    public string Protect(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Credential value is required.", nameof(value));
        return _protector.Protect(value);
    }

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
            throw new ArgumentException("Protected credential value is required.", nameof(protectedValue));
        return _protector.Unprotect(protectedValue);
    }
}
