using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Dhole.Agent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class BrowserProfileConfiguration : EntityTypeConfigurationBase<BrowserProfile, Guid>
{
    public override void Configure(EntityTypeBuilder<BrowserProfile> builder)
    {
        base.Configure(builder);
        builder.ToTable("BrowserProfiles");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ProfileKey).HasMaxLength(250).IsRequired();
        builder.HasIndex(x => x.ProfileKey).IsUnique();
        builder.Property(x => x.StoragePath).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.HasOne<AgentProvider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<AgentCredential>().WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.ProviderId, x.CredentialId });
        builder.Property(x => x.IsActive).IsRequired();
    }
}
