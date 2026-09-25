using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dhole.Agent.Persistence.Migrations;

[DbContext(typeof(ServiceDbContext))]
[Migration("20260925132000_AddScheduleExtractionProfile")]
public sealed class AddScheduleExtractionProfile : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "extraction_profile_id",
            schema: "agent",
            table: "AgentSchedules",
            type: "uuid",
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE agent."AgentSchedules" AS s
            SET extraction_profile_id = (
                SELECT ep.id
                FROM agent.extraction_profiles AS ep
                WHERE ep.provider_id = s.provider_id
                  AND ep.is_active = TRUE
                  AND ep.is_deleted = FALSE
                  AND (ep.credential_id = s.credential_id OR ep.credential_id IS NULL)
                ORDER BY
                    CASE WHEN ep.credential_id = s.credential_id THEN 0 ELSE 1 END,
                    ep.created_at_utc
                LIMIT 1
            )
            WHERE s.extraction_profile_id IS NULL;
            """);

        // Force current Cron/Interval schedules to calculate a fresh next occurrence
        // from cron_expression + timezone after this deployment.
        migrationBuilder.Sql("""
            UPDATE agent."AgentSchedules"
            SET next_execution_at = NULL
            WHERE schedule_type IN ('Cron', 'Interval');
            """);

        migrationBuilder.CreateIndex(
            name: "IX_AgentSchedules_extraction_profile_id",
            schema: "agent",
            table: "AgentSchedules",
            column: "extraction_profile_id");

        migrationBuilder.AddForeignKey(
            name: "f_k_agent_schedules_extraction_profiles_extraction_profile_id",
            schema: "agent",
            table: "AgentSchedules",
            column: "extraction_profile_id",
            principalSchema: "agent",
            principalTable: "extraction_profiles",
            principalColumn: "id",
            onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "f_k_agent_schedules_extraction_profiles_extraction_profile_id",
            schema: "agent",
            table: "AgentSchedules");

        migrationBuilder.DropIndex(
            name: "IX_AgentSchedules_extraction_profile_id",
            schema: "agent",
            table: "AgentSchedules");

        migrationBuilder.DropColumn(
            name: "extraction_profile_id",
            schema: "agent",
            table: "AgentSchedules");
    }
}
