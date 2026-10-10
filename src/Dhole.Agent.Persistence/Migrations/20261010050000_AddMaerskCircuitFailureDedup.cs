using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dhole.Agent.Persistence.Migrations;

[DbContext(typeof(ServiceDbContext))]
[Migration("20261010050000_AddMaerskCircuitFailureDedup")]
public sealed class AddMaerskCircuitFailureDedup : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE agent.maersk_circuit_events
                ADD COLUMN IF NOT EXISTS execution_id uuid NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ix_maersk_circuit_events_failure_execution
                ON agent.maersk_circuit_events(provider_id,execution_id,event_type)
                WHERE execution_id IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Keep incident evidence on rollback. The feature is disabled by flag.
    }
}
