using Dhole.Agent.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dhole.Agent.Persistence.Migrations;

[DbContext(typeof(ServiceDbContext))]
[Migration("20260928003000_AddExtractionProfileAuthenticationSuccessUrl")]
public sealed class AddExtractionProfileAuthenticationSuccessUrl : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "authentication_success_url",
            schema: "agent",
            table: "extraction_profiles",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE agent.extraction_profiles AS ep
            SET
                login_url = CASE
                    WHEN ep.login_url IS NULL OR BTRIM(ep.login_url) = ''
                        THEN 'https://www.maersk.com/portaluser/login'
                    ELSE ep.login_url
                END,
                authentication_success_url = 'https://www.maersk.com/portaluser/oidc/callback',
                search_url = CASE
                    WHEN ep.search_url IS NULL
                      OR BTRIM(ep.search_url) = ''
                      OR ep.search_url ILIKE 'https://api.maersk.com/%'
                        THEN 'https://www.maersk.com/book/'
                    ELSE ep.search_url
                END
            FROM agent."AgentProviders" AS p
            WHERE ep.provider_id = p.id
              AND UPPER(p.code) = 'MAERSK';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "authentication_success_url",
            schema: "agent",
            table: "extraction_profiles");
    }
}
