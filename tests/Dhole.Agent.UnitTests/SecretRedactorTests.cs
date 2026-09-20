using Dhole.Agent.Infrastructure.Secrets;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class SecretRedactorTests
{
    [TestMethod]
    public void Redact_ShouldRemoveBearerToken()
    {
        var value = "Authorization: Bearer abc.def-123_XYZ";

        var redacted = new SecretRedactor().Redact(value);

        StringAssert.Contains(redacted, "Bearer [REDACTED]");
        Assert.IsFalse(redacted.Contains("abc.def-123_XYZ", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Redact_ShouldRemoveJsonPasswordAndCookie()
    {
        var value = """{"password":"S3cret!","cookie":"session=abc123","safe":"ok"}""";

        var redacted = new SecretRedactor().Redact(value);

        Assert.IsFalse(redacted.Contains("S3cret!", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("session=abc123", StringComparison.Ordinal));
        StringAssert.Contains(redacted, "[REDACTED]");
        StringAssert.Contains(redacted, ""safe":"ok"");
    }

    [TestMethod]
    public void Redact_ShouldHandleNullAndEmpty()
    {
        var redactor = new SecretRedactor();

        Assert.AreEqual(string.Empty, redactor.Redact(null));
        Assert.AreEqual(string.Empty, redactor.Redact(string.Empty));
    }
}
