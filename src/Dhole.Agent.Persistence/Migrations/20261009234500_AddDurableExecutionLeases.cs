using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dhole.Agent.Persistence.Migrations;

[DbContext(typeof(ServiceDbContext))]
[Migration("20261009234500_AddDurableExecutionLeases")]
public sealed class AddDurableExecutionLeases : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE agent."AgentExecutions"
                ADD COLUMN IF NOT EXISTS next_attempt_at_utc timestamp with time zone NULL;

            CREATE TABLE IF NOT EXISTS agent.execution_leases (
                lease_scope varchar(160) PRIMARY KEY,
                execution_id uuid NOT NULL,
                owner_id uuid NOT NULL,
                expires_at_utc timestamp with time zone NOT NULL,
                heartbeat_at_utc timestamp with time zone NOT NULL,
                CONSTRAINT fk_agent_execution_lease
                    FOREIGN KEY (execution_id)
                    REFERENCES agent."AgentExecutions" (id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_execution_leases_execution
                ON agent.execution_leases (execution_id);
            CREATE INDEX IF NOT EXISTS ix_agent_execution_leases_expiry
                ON agent.execution_leases (expires_at_utc);
            CREATE INDEX IF NOT EXISTS ix_agent_execution_queue_ready
                ON agent."AgentExecutions" (next_attempt_at_utc, created_at_utc)
                WHERE status = 'Queued';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Production-safe rollback is disabling ConcurrentDispatcherEnabled.
        // Preserve lease rows and delayed retry timestamps for investigation and
        // forward migration rather than destructively dropping job ownership data.
    }
}
