namespace Dhole.Agent.Application.Abstractions.Security;

public interface ICredentialProtector
{
    string Protect(string value);
    string Unprotect(string protectedValue);
}
