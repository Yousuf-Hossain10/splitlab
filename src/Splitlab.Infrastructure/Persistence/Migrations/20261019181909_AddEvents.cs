using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Splitlab.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExperimentKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    VariationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Value = table.Column<double>(type: "double precision", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => new { x.EnvironmentId, x.EventId, x.OccurredAt });
                });

            migrationBuilder.CreateIndex(
                name: "IX_events_EnvironmentId_ExperimentKey_Kind",
                table: "events",
                columns: new[] { "EnvironmentId", "ExperimentKey", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_events_EnvironmentId_OccurredAt",
                table: "events",
                columns: new[] { "EnvironmentId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_events_ProjectId",
                table: "events",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events");
        }
    }
}
