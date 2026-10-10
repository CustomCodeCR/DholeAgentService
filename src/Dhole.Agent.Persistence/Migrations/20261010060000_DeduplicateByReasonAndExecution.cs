using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dhole.Agent.Persistence.Migrations;

[DbContext(typeof(ServiceDbContext))]
[Migration("20261010060000_DeduplicateByReasonAndExecution")]
public sealed class DeduplicateByReasonAndExecution : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP INDEX IF EXISTS agent.ix_maersk_circuit_events_failure_execution;
            CREATE UNIQUE INDEX IF NOT EXISTS ix_maersk_circuit_events_failure_reason
                ON agent.maersk_circuit_events(provider_id, execution_id, event_type, reason_code)
                WHERE execution_id IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Keep the stronger, reason-aware idempotency index on rollback.
    }
}
