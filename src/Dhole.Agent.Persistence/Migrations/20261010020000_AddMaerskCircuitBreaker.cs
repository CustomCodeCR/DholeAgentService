using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dhole.Agent.Persistence.Migrations;

[DbContext(typeof(ServiceDbContext))]
[Migration("20261010020000_AddMaerskCircuitBreaker")]
public sealed class AddMaerskCircuitBreaker : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS agent.maersk_circuits (
                provider_id uuid PRIMARY KEY REFERENCES agent."AgentProviders"(id) ON DELETE CASCADE,
                state varchar(20) NOT NULL DEFAULT 'Closed',
                requires_operator boolean NOT NULL DEFAULT false,
                reason_code varchar(120) NULL,
                open_until_utc timestamptz NULL,
                consecutive_failures integer NOT NULL DEFAULT 0,
                probe_execution_id uuid NULL,
                updated_at_utc timestamptz NOT NULL DEFAULT NOW(),
                CONSTRAINT ck_maersk_circuits_state CHECK (state IN ('Closed','Open','HalfOpen'))
            );
            CREATE TABLE IF NOT EXISTS agent.maersk_circuit_events (
                id uuid PRIMARY KEY,
                provider_id uuid NOT NULL REFERENCES agent."AgentProviders"(id) ON DELETE CASCADE,
                event_type varchar(40) NOT NULL,
                reason_code varchar(120) NULL,
                actor_id uuid NULL,
                operator_reason varchar(1000) NULL,
                occurred_at_utc timestamptz NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS ix_maersk_circuit_events_provider_occurred
                ON agent.maersk_circuit_events(provider_id,occurred_at_utc DESC);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Keep incident/audit history on feature rollback; disable MaerskCircuit:Enabled
        // rather than dropping provider-side verification records.
    }
}
