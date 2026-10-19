using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dhole.Agent.Persistence.Migrations;

/// <summary>
/// Shared Agent data and isolated integration databases must agree about which
/// numbered production generation owns browser session records.
/// Existing prod2/prodN ownership is never overwritten.
/// </summary>
[DbContext(typeof(ServiceDbContext))]
[Migration("20261019010000_InitializeRuntimeGenerationState")]
public sealed class InitializeRuntimeGenerationState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS agent.runtime_generation_state (
                singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                active_generation text NOT NULL DEFAULT 'prod',
                updated_at_utc timestamptz NOT NULL DEFAULT now()
            );
            INSERT INTO agent.runtime_generation_state(singleton, active_generation)
            VALUES (true, 'prod') ON CONFLICT(singleton) DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Runtime generation ownership is operational state. Do not drop the
        // singleton table or reset active prodN during a migration rollback.
    }
}
