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
            name: "ExtractionProfileId",
            schema: "agent",
            table: "AgentSchedules",
            type: "uuid",
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE agent."AgentSchedules" AS s
            SET "ExtractionProfileId" = (
                SELECT ep.id
                FROM agent.extraction_profiles AS ep
                WHERE ep.provider_id = s."ProviderId"
                  AND ep.is_active = TRUE
                  AND ep.is_deleted = FALSE
                  AND (ep.credential_id = s."CredentialId" OR ep.credential_id IS NULL)
                ORDER BY
                    CASE WHEN ep.credential_id = s."CredentialId" THEN 0 ELSE 1 END,
                    ep.created_at_utc
                LIMIT 1
            )
            WHERE s."ExtractionProfileId" IS NULL;
            """);

        // Existing Cron/Interval schedules may contain a stale or missing next occurrence.
        // Let the dispatcher initialize them again from CronExpression + Timezone.
        migrationBuilder.Sql("""
            UPDATE agent."AgentSchedules"
            SET "NextExecutionAt" = NULL
            WHERE "ScheduleType" IN ('Cron', 'Interval');
            """);

        migrationBuilder.CreateIndex(
            name: "IX_AgentSchedules_ExtractionProfileId",
            schema: "agent",
            table: "AgentSchedules",
            column: "ExtractionProfileId");

        migrationBuilder.AddForeignKey(
            name: "FK_AgentSchedules_extraction_profiles_ExtractionProfileId",
            schema: "agent",
            table: "AgentSchedules",
            column: "ExtractionProfileId",
            principalSchema: "agent",
            principalTable: "extraction_profiles",
            principalColumn: "id",
            onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_AgentSchedules_extraction_profiles_ExtractionProfileId",
            schema: "agent",
            table: "AgentSchedules");

        migrationBuilder.DropIndex(
            name: "IX_AgentSchedules_ExtractionProfileId",
            schema: "agent",
            table: "AgentSchedules");

        migrationBuilder.DropColumn(
            name: "ExtractionProfileId",
            schema: "agent",
            table: "AgentSchedules");
    }
}
