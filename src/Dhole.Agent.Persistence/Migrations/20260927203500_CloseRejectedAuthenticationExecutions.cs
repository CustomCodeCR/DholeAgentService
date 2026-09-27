using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dhole.Agent.Persistence.Migrations;

[DbContext(typeof(ServiceDbContext))]
[Migration("20260927203500_CloseRejectedAuthenticationExecutions")]
public sealed class CloseRejectedAuthenticationExecutions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE agent."AgentExecutions"
            SET
                status = 'Failed',
                completed_at = COALESCE(completed_at, NOW()),
                duration_ms = CASE
                    WHEN started_at IS NULL THEN duration_ms
                    ELSE GREATEST(
                        0,
                        FLOOR(EXTRACT(EPOCH FROM (COALESCE(completed_at, NOW()) - started_at)) * 1000)
                    )::bigint
                END,
                updated_at_utc = NOW()
            WHERE status = 'WaitingForAuthentication'
              AND error_code IN (
                  'maersk_authentication_forbidden',
                  'maersk_authentication_unauthorized',
                  'maersk_authentication_rate_limited',
                  'maersk_authentication_service_error',
                  'maersk_authentication_failed',
                  'maersk_authentication_blocked'
              );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Data correction only. Completed/failed executions must not be reopened automatically.
    }
}
