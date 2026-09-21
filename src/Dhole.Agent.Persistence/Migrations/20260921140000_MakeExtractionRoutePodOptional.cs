using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Dhole.Agent.Persistence.Migrations;

[DbContext(typeof(ServiceDbContext))]
[Migration("20260921140000_MakeExtractionRoutePodOptional")]
public sealed class MakeExtractionRoutePodOptional : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "pod_name", schema: "agent", table: "extraction_routes",
            type: "character varying(300)", maxLength: 300, nullable: true,
            oldClrType: typeof(string), oldType: "character varying(300)", oldMaxLength: 300);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Preserve routes created without a POD when restoring the old schema.
        migrationBuilder.Sql("UPDATE agent.extraction_routes SET pod_name = '' WHERE pod_name IS NULL;");
        migrationBuilder.AlterColumn<string>(
            name: "pod_name", schema: "agent", table: "extraction_routes",
            type: "character varying(300)", maxLength: 300, nullable: false,
            oldClrType: typeof(string), oldType: "character varying(300)", oldMaxLength: 300,
            oldNullable: true);
    }
}
