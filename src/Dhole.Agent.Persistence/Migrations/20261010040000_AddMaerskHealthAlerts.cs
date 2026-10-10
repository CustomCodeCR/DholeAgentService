using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dhole.Agent.Persistence.Migrations;

[DbContext(typeof(ServiceDbContext))]
[Migration("20261010040000_AddMaerskHealthAlerts")]
public sealed class AddMaerskHealthAlerts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS agent.maersk_health_alerts (
                id uuid PRIMARY KEY,
                provider_id uuid NOT NULL REFERENCES agent."AgentProviders"(id) ON DELETE CASCADE,
                alert_key varchar(80) NOT NULL,
                code varchar(120) NOT NULL,
                severity varchar(16) NOT NULL,
                state varchar(16) NOT NULL DEFAULT 'Active',
                occurrences integer NOT NULL DEFAULT 1,
                first_seen_at_utc timestamptz NOT NULL DEFAULT NOW(),
                last_seen_at_utc timestamptz NOT NULL DEFAULT NOW(),
                resolved_at_utc timestamptz NULL,
                acknowledged_at_utc timestamptz NULL,
                acknowledged_by uuid NULL,
                CONSTRAINT ck_maersk_alert_severity CHECK (severity IN ('Warning','Critical')),
                CONSTRAINT ck_maersk_alert_state CHECK (state IN ('Active','Resolved')),
                CONSTRAINT ck_maersk_alert_occurrences CHECK (occurrences >= 1)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_maersk_health_alert_dedup
                ON agent.maersk_health_alerts(provider_id, alert_key);
            CREATE INDEX IF NOT EXISTS ix_maersk_health_alert_state
                ON agent.maersk_health_alerts(provider_id, state, last_seen_at_utc DESC);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Operational audit/acknowledgement history is intentionally preserved.
        // Roll back with MaerskMonitoring:Enabled=false.
    }
}
