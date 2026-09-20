using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Dhole.Agent.Persistence.DbContexts;

#nullable disable

namespace Dhole.Agent.Persistence.Migrations;

[DbContext(typeof(ServiceDbContext))]
[Migration("20260920150000_InitialAgentSchema")]
public sealed class InitialAgentSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "agent");

        migrationBuilder.CreateTable(
            name: "outbox_messages",
            schema: "agent",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                event_type = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                event_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                source_service = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                payload_json = table.Column<string>(type: "jsonb", nullable: false),
                headers_json = table.Column<string>(type: "jsonb", nullable: true),
                correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                retry_count = table.Column<int>(type: "integer", nullable: false),
                error_message = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_outbox_messages", x => x.Id));

        migrationBuilder.CreateTable(
            name: "inbox_messages",
            schema: "agent",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                event_id = table.Column<Guid>(type: "uuid", nullable: false),
                event_type = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                event_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                source_service = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                consumer_service = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_inbox_messages", x => x.Id));

        migrationBuilder.CreateTable(
            name: "AgentProviders",
            schema: "agent",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                ProviderType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                BaseUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                DefaultExecutionStrategy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                MetadataJson = table.Column<string>(type: "jsonb", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "text", nullable: true),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                UpdatedBy = table.Column<string>(type: "text", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DeletedBy = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_AgentProviders", x => x.Id));

        migrationBuilder.CreateTable(
            name: "AgentDefinitions", schema: "agent",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                ActionType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                ExecutionStrategy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                ConfigurationJson = table.Column<string>(type: "jsonb", nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "text", nullable: true),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                UpdatedBy = table.Column<string>(type: "text", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DeletedBy = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AgentDefinitions", x => x.Id);
                table.ForeignKey(name: "FK_AgentDefinitions_AgentProviders_ProviderId", column: x => x.ProviderId, principalSchema: "agent", principalTable: "AgentProviders", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "AgentCredentials", schema: "agent",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                UsernameSecretKey = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                PasswordSecretKey = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                AdditionalSecretsJson = table.Column<string>(type: "jsonb", nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "text", nullable: true),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                UpdatedBy = table.Column<string>(type: "text", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DeletedBy = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AgentCredentials", x => x.Id);
                table.ForeignKey(name: "FK_AgentCredentials_AgentProviders_ProviderId", column: x => x.ProviderId, principalSchema: "agent", principalTable: "AgentProviders", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "BrowserProfiles", schema: "agent",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                ProfileKey = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                StoragePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                LastLoginAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                SessionExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "text", nullable: true),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                UpdatedBy = table.Column<string>(type: "text", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DeletedBy = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BrowserProfiles", x => x.Id);
                table.ForeignKey(name: "FK_BrowserProfiles_AgentProviders_ProviderId", column: x => x.ProviderId, principalSchema: "agent", principalTable: "AgentProviders", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey(name: "FK_BrowserProfiles_AgentCredentials_CredentialId", column: x => x.CredentialId, principalSchema: "agent", principalTable: "AgentCredentials", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "AgentSchedules", schema: "agent",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                AgentDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                CredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                ScheduleType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                CronExpression = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                IntervalMinutes = table.Column<int>(type: "integer", nullable: true),
                ExecuteAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Timezone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                InputJson = table.Column<string>(type: "jsonb", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                LastExecutionAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                NextExecutionAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                MaxRetries = table.Column<int>(type: "integer", nullable: false),
                TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "text", nullable: true),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                UpdatedBy = table.Column<string>(type: "text", nullable: true),
                IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                DeletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DeletedBy = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AgentSchedules", x => x.Id);
                table.ForeignKey(name: "FK_AgentSchedules_AgentDefinitions_AgentDefinitionId", column: x => x.AgentDefinitionId, principalSchema: "agent", principalTable: "AgentDefinitions", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey(name: "FK_AgentSchedules_AgentProviders_ProviderId", column: x => x.ProviderId, principalSchema: "agent", principalTable: "AgentProviders", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey(name: "FK_AgentSchedules_AgentCredentials_CredentialId", column: x => x.CredentialId, principalSchema: "agent", principalTable: "AgentCredentials", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "AgentExecutions", schema: "agent",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AgentDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                ScheduleId = table.Column<Guid>(type: "uuid", nullable: true),
                CredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                ExecutionType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Status = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                Priority = table.Column<int>(type: "integer", nullable: false),
                InputJson = table.Column<string>(type: "jsonb", nullable: false),
                OutputJson = table.Column<string>(type: "jsonb", nullable: true),
                StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DurationMs = table.Column<long>(type: "bigint", nullable: true),
                Attempt = table.Column<int>(type: "integer", nullable: false),
                MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                ErrorCode = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                ErrorMessage = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                TraceId = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "text", nullable: true),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                UpdatedBy = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AgentExecutions", x => x.Id);
                table.ForeignKey(name: "FK_AgentExecutions_AgentDefinitions_AgentDefinitionId", column: x => x.AgentDefinitionId, principalSchema: "agent", principalTable: "AgentDefinitions", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey(name: "FK_AgentExecutions_AgentProviders_ProviderId", column: x => x.ProviderId, principalSchema: "agent", principalTable: "AgentProviders", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey(name: "FK_AgentExecutions_AgentSchedules_ScheduleId", column: x => x.ScheduleId, principalSchema: "agent", principalTable: "AgentSchedules", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey(name: "FK_AgentExecutions_AgentCredentials_CredentialId", column: x => x.CredentialId, principalSchema: "agent", principalTable: "AgentCredentials", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "AgentExecutionLogs", schema: "agent",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                Level = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                Category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                PayloadJson = table.Column<string>(type: "jsonb", nullable: true),
                OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "text", nullable: true),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                UpdatedBy = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AgentExecutionLogs", x => x.Id);
                table.ForeignKey(name: "FK_AgentExecutionLogs_AgentExecutions_ExecutionId", column: x => x.ExecutionId, principalSchema: "agent", principalTable: "AgentExecutions", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AgentResults", schema: "agent",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ExecutionId = table.Column<Guid>(type: "uuid", nullable: false),
                ProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                ResultType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                SchemaVersion = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                DataJson = table.Column<string>(type: "jsonb", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<string>(type: "text", nullable: true),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                UpdatedBy = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AgentResults", x => x.Id);
                table.ForeignKey(name: "FK_AgentResults_AgentExecutions_ExecutionId", column: x => x.ExecutionId, principalSchema: "agent", principalTable: "AgentExecutions", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey(name: "FK_AgentResults_AgentProviders_ProviderId", column: x => x.ProviderId, principalSchema: "agent", principalTable: "AgentProviders", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(name: "IX_outbox_messages_event_id", schema: "agent", table: "outbox_messages", column: "event_id", unique: true);
        migrationBuilder.CreateIndex(name: "IX_outbox_messages_status_created_at", schema: "agent", table: "outbox_messages", columns: new[] { "status", "created_at" });
        migrationBuilder.CreateIndex(name: "IX_inbox_messages_event_id_consumer_service", schema: "agent", table: "inbox_messages", columns: new[] { "event_id", "consumer_service" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_inbox_messages_status_created_at", schema: "agent", table: "inbox_messages", columns: new[] { "status", "created_at" });
        migrationBuilder.CreateIndex(name: "IX_AgentProviders_Code", schema: "agent", table: "AgentProviders", column: "Code", unique: true);
        migrationBuilder.CreateIndex(name: "IX_AgentDefinitions_Code", schema: "agent", table: "AgentDefinitions", column: "Code", unique: true);
        migrationBuilder.CreateIndex(name: "IX_AgentDefinitions_ProviderId", schema: "agent", table: "AgentDefinitions", column: "ProviderId");
        migrationBuilder.CreateIndex(name: "IX_AgentCredentials_ProviderId", schema: "agent", table: "AgentCredentials", column: "ProviderId");
        migrationBuilder.CreateIndex(name: "IX_BrowserProfiles_ProfileKey", schema: "agent", table: "BrowserProfiles", column: "ProfileKey", unique: true);
        migrationBuilder.CreateIndex(name: "IX_BrowserProfiles_ProviderId_CredentialId", schema: "agent", table: "BrowserProfiles", columns: new[] { "ProviderId", "CredentialId" });
        migrationBuilder.CreateIndex(name: "IX_AgentSchedules_NextExecutionAt", schema: "agent", table: "AgentSchedules", column: "NextExecutionAt");
        migrationBuilder.CreateIndex(name: "IX_AgentSchedules_IsActive", schema: "agent", table: "AgentSchedules", column: "IsActive");
        migrationBuilder.CreateIndex(name: "IX_AgentSchedules_AgentDefinitionId", schema: "agent", table: "AgentSchedules", column: "AgentDefinitionId");
        migrationBuilder.CreateIndex(name: "IX_AgentSchedules_ProviderId", schema: "agent", table: "AgentSchedules", column: "ProviderId");
        migrationBuilder.CreateIndex(name: "IX_AgentSchedules_CredentialId", schema: "agent", table: "AgentSchedules", column: "CredentialId");
        migrationBuilder.CreateIndex(name: "IX_AgentExecutions_Status", schema: "agent", table: "AgentExecutions", column: "Status");
        migrationBuilder.CreateIndex(name: "IX_AgentExecutions_ProviderId", schema: "agent", table: "AgentExecutions", column: "ProviderId");
        migrationBuilder.CreateIndex(name: "IX_AgentExecutions_CreatedAtUtc", schema: "agent", table: "AgentExecutions", column: "CreatedAtUtc");
        migrationBuilder.CreateIndex(name: "IX_AgentExecutions_AgentDefinitionId", schema: "agent", table: "AgentExecutions", column: "AgentDefinitionId");
        migrationBuilder.CreateIndex(name: "IX_AgentExecutions_ScheduleId", schema: "agent", table: "AgentExecutions", column: "ScheduleId");
        migrationBuilder.CreateIndex(name: "IX_AgentExecutions_CredentialId", schema: "agent", table: "AgentExecutions", column: "CredentialId");
        migrationBuilder.CreateIndex(name: "IX_AgentExecutionLogs_ExecutionId_OccurredAt", schema: "agent", table: "AgentExecutionLogs", columns: new[] { "ExecutionId", "OccurredAt" });
        migrationBuilder.CreateIndex(name: "IX_AgentResults_ExecutionId", schema: "agent", table: "AgentResults", column: "ExecutionId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_AgentResults_ProviderId", schema: "agent", table: "AgentResults", column: "ProviderId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AgentResults", schema: "agent");
        migrationBuilder.DropTable(name: "AgentExecutionLogs", schema: "agent");
        migrationBuilder.DropTable(name: "AgentExecutions", schema: "agent");
        migrationBuilder.DropTable(name: "BrowserProfiles", schema: "agent");
        migrationBuilder.DropTable(name: "AgentSchedules", schema: "agent");
        migrationBuilder.DropTable(name: "AgentCredentials", schema: "agent");
        migrationBuilder.DropTable(name: "AgentDefinitions", schema: "agent");
        migrationBuilder.DropTable(name: "AgentProviders", schema: "agent");
        migrationBuilder.DropTable(name: "inbox_messages", schema: "agent");
        migrationBuilder.DropTable(name: "outbox_messages", schema: "agent");
    }
}
