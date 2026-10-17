using System.Net.Mail;

namespace Dhole.Agent.Application.Runtime;

/// <summary>
/// Routes Maersk circuit state change emails through the existing outbox.
/// </summary>
public sealed class MaerskCircuitNotificationsOptions
{
    public const string SectionName = "MaerskCircuitNotifications";
    public bool Enabled { get; set; }
    public string To { get; set; } = string.Empty;
    public string[] Cc { get; set; } = [];

    public void Validate()
    {
        if (!MailAddress.TryCreate(To, out _))
            throw new InvalidOperationException("Invalid primary recipient.");
        foreach (var email in Cc)
            if (!MailAddress.TryCreate(email, out _))
                throw new InvalidOperationException("Invalid CC recipient.");
    }
}
