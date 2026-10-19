using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Splitlab.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthAndTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_environments_Key",
                table: "environments");

            migrationBuilder.DropIndex(
                name: "IX_environments_ProjectId",
                table: "environments");

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Prefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Permissions = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "project_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_members", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_environments_ProjectId_Key",
                table: "environments",
                columns: new[] { "ProjectId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_EnvironmentId",
                table: "api_keys",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_Prefix",
                table: "api_keys",
                column: "Prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_members_ProjectId_Subject",
                table: "project_members",
                columns: new[] { "ProjectId", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_members_Subject",
                table: "project_members",
                column: "Subject");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "project_members");

            migrationBuilder.DropIndex(
                name: "IX_environments_ProjectId_Key",
                table: "environments");

            migrationBuilder.CreateIndex(
                name: "IX_environments_Key",
                table: "environments",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_environments_ProjectId",
                table: "environments",
                column: "ProjectId");
        }
    }
}
