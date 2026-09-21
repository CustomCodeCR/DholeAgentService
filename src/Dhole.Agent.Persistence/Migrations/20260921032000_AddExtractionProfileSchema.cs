using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dhole.Agent.Persistence.Migrations;

[DbContext(typeof(ServiceDbContext))]
[Migration("20260921032000_AddExtractionProfileSchema")]
public sealed class AddExtractionProfileSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Encrypted credentials introduced after the initial schema.
        migrationBuilder.AddColumn<string>(
            name: "username_encrypted",
            schema: "agent",
            table: "AgentCredentials",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "password_encrypted",
            schema: "agent",
            table: "AgentCredentials",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "additional_secrets_encrypted",
            schema: "agent",
            table: "AgentCredentials",
            type: "text",
            nullable: true);

        // Legacy secret references remain readable, but encrypted credentials no longer populate them.
        migrationBuilder.AlterColumn<string>(
            name: "username_secret_key",
            schema: "agent",
            table: "AgentCredentials",
            type: "character varying(250)",
            maxLength: 250,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(250)",
            oldMaxLength: 250);

        migrationBuilder.AlterColumn<string>(
            name: "password_secret_key",
            schema: "agent",
            table: "AgentCredentials",
            type: "character varying(250)",
            maxLength: 250,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(250)",
            oldMaxLength: 250);

        migrationBuilder.CreateTable(
            name: "extraction_profiles",
            schema: "agent",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                base_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                login_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                search_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                prompt_template = table.Column<string>(type: "text", nullable: false),
                execution_strategy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                parser_key = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                created_by = table.Column<string>(type: "text", nullable: true),
                updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                updated_by = table.Column<string>(type: "text", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                deleted_by = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("p_k_extraction_profiles", x => x.id);
                table.ForeignKey(
                    name: "f_k_extraction_profiles_agent_providers_provider_id",
                    column: x => x.provider_id,
                    principalSchema: "agent",
                    principalTable: "AgentProviders",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "f_k_extraction_profiles_agent_credentials_credential_id",
                    column: x => x.credential_id,
                    principalSchema: "agent",
                    principalTable: "AgentCredentials",
                    principalColumn: "id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_extraction_profiles_provider_id",
            schema: "agent",
            table: "extraction_profiles",
            column: "provider_id");

        migrationBuilder.CreateIndex(
            name: "IX_extraction_profiles_credential_id",
            schema: "agent",
            table: "extraction_profiles",
            column: "credential_id");

        migrationBuilder.CreateIndex(
            name: "IX_extraction_profiles_is_active",
            schema: "agent",
            table: "extraction_profiles",
            column: "is_active");

        migrationBuilder.CreateTable(
            name: "extraction_routes",
            schema: "agent",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                pol_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                pol_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                poe_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                poe_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                pod_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                pod_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                sort_order = table.Column<int>(type: "integer", nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                created_by = table.Column<string>(type: "text", nullable: true),
                updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                updated_by = table.Column<string>(type: "text", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                deleted_by = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("p_k_extraction_routes", x => x.id);
                table.ForeignKey(
                    name: "f_k_extraction_routes_extraction_profiles_profile_id",
                    column: x => x.profile_id,
                    principalSchema: "agent",
                    principalTable: "extraction_profiles",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_extraction_routes_profile_id_sort_order",
            schema: "agent",
            table: "extraction_routes",
            columns: new[] { "profile_id", "sort_order" });

        migrationBuilder.CreateTable(
            name: "extraction_equipment",
            schema: "agent",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                quantity = table.Column<int>(type: "integer", nullable: false),
                default_weight_kg = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                sort_order = table.Column<int>(type: "integer", nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                created_by = table.Column<string>(type: "text", nullable: true),
                updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                updated_by = table.Column<string>(type: "text", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                deleted_by = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("p_k_extraction_equipment", x => x.id);
                table.ForeignKey(
                    name: "f_k_extraction_equipment_extraction_profiles_profile_id",
                    column: x => x.profile_id,
                    principalSchema: "agent",
                    principalTable: "extraction_profiles",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_extraction_equipment_profile_id_code",
            schema: "agent",
            table: "extraction_equipment",
            columns: new[] { "profile_id", "code" });

        migrationBuilder.CreateIndex(
            name: "IX_extraction_equipment_profile_id_sort_order",
            schema: "agent",
            table: "extraction_equipment",
            columns: new[] { "profile_id", "sort_order" });

        migrationBuilder.CreateTable(
            name: "endpoint_captures",
            schema: "agent",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                http_method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                url_pattern = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                match_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                capture_request = table.Column<bool>(type: "boolean", nullable: false),
                capture_response = table.Column<bool>(type: "boolean", nullable: false),
                is_required = table.Column<bool>(type: "boolean", nullable: false),
                timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                sort_order = table.Column<int>(type: "integer", nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                created_by = table.Column<string>(type: "text", nullable: true),
                updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                updated_by = table.Column<string>(type: "text", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                deleted_by = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("p_k_endpoint_captures", x => x.id);
                table.ForeignKey(
                    name: "f_k_endpoint_captures_extraction_profiles_profile_id",
                    column: x => x.profile_id,
                    principalSchema: "agent",
                    principalTable: "extraction_profiles",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_endpoint_captures_profile_id_sort_order",
            schema: "agent",
            table: "endpoint_captures",
            columns: new[] { "profile_id", "sort_order" });

        migrationBuilder.CreateTable(
            name: "extraction_fields",
            schema: "agent",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                key = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                label = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                data_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                source_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                json_path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                required = table.Column<bool>(type: "boolean", nullable: false),
                sort_order = table.Column<int>(type: "integer", nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false),
                created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                created_by = table.Column<string>(type: "text", nullable: true),
                updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                updated_by = table.Column<string>(type: "text", nullable: true),
                is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                deleted_by = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("p_k_extraction_fields", x => x.id);
                table.ForeignKey(
                    name: "f_k_extraction_fields_extraction_profiles_profile_id",
                    column: x => x.profile_id,
                    principalSchema: "agent",
                    principalTable: "extraction_profiles",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_extraction_fields_profile_id_key",
            schema: "agent",
            table: "extraction_fields",
            columns: new[] { "profile_id", "key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_extraction_fields_profile_id_sort_order",
            schema: "agent",
            table: "extraction_fields",
            columns: new[] { "profile_id", "sort_order" });

        migrationBuilder.AddColumn<Guid>(
            name: "extraction_profile_id",
            schema: "agent",
            table: "AgentExecutions",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "prompt_snapshot",
            schema: "agent",
            table: "AgentExecutions",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "configuration_snapshot_json",
            schema: "agent",
            table: "AgentExecutions",
            type: "jsonb",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_AgentExecutions_extraction_profile_id",
            schema: "agent",
            table: "AgentExecutions",
            column: "extraction_profile_id");

        migrationBuilder.AddForeignKey(
            name: "f_k_agent_executions_extraction_profiles_extraction_profile_id",
            schema: "agent",
            table: "AgentExecutions",
            column: "extraction_profile_id",
            principalSchema: "agent",
            principalTable: "extraction_profiles",
            principalColumn: "id",
            onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "f_k_agent_executions_extraction_profiles_extraction_profile_id",
            schema: "agent",
            table: "AgentExecutions");

        migrationBuilder.DropIndex(
            name: "IX_AgentExecutions_extraction_profile_id",
            schema: "agent",
            table: "AgentExecutions");

        migrationBuilder.DropColumn(
            name: "extraction_profile_id",
            schema: "agent",
            table: "AgentExecutions");

        migrationBuilder.DropColumn(
            name: "prompt_snapshot",
            schema: "agent",
            table: "AgentExecutions");

        migrationBuilder.DropColumn(
            name: "configuration_snapshot_json",
            schema: "agent",
            table: "AgentExecutions");

        migrationBuilder.DropTable(name: "endpoint_captures", schema: "agent");
        migrationBuilder.DropTable(name: "extraction_equipment", schema: "agent");
        migrationBuilder.DropTable(name: "extraction_fields", schema: "agent");
        migrationBuilder.DropTable(name: "extraction_routes", schema: "agent");
        migrationBuilder.DropTable(name: "extraction_profiles", schema: "agent");

        migrationBuilder.AlterColumn<string>(
            name: "username_secret_key",
            schema: "agent",
            table: "AgentCredentials",
            type: "character varying(250)",
            maxLength: 250,
            nullable: false,
            defaultValue: "",
            oldClrType: typeof(string),
            oldType: "character varying(250)",
            oldMaxLength: 250,
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "password_secret_key",
            schema: "agent",
            table: "AgentCredentials",
            type: "character varying(250)",
            maxLength: 250,
            nullable: false,
            defaultValue: "",
            oldClrType: typeof(string),
            oldType: "character varying(250)",
            oldMaxLength: 250,
            oldNullable: true);

        migrationBuilder.DropColumn(
            name: "username_encrypted",
            schema: "agent",
            table: "AgentCredentials");

        migrationBuilder.DropColumn(
            name: "password_encrypted",
            schema: "agent",
            table: "AgentCredentials");

        migrationBuilder.DropColumn(
            name: "additional_secrets_encrypted",
            schema: "agent",
            table: "AgentCredentials");
    }
}
