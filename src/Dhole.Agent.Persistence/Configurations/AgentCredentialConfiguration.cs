using CustomCodeFramework.Postgres.EntityFramework.Configurations;
using Dhole.Agent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Dhole.Agent.Persistence.Configurations;

internal sealed class AgentCredentialConfiguration : EntityTypeConfigurationBase<AgentCredential, Guid>
{
    public override void Configure(EntityTypeBuilder<AgentCredential> builder)
    {
        base.Configure(builder);
        builder.ToTable("AgentCredentials");
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.UsernameSecretKey).HasMaxLength(250).IsRequired();
        builder.Property(x => x.PasswordSecretKey).HasMaxLength(250).IsRequired();
        builder.Property(x => x.AdditionalSecretsJson).HasColumnType("jsonb");
        builder.HasOne<AgentProvider>().WithMany().HasForeignKey(x => x.ProviderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.ProviderId);
        builder.Property(x => x.IsActive).IsRequired();
    }
}
