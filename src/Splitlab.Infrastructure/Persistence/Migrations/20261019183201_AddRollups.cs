using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Splitlab.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "experiment_user_states",
                columns: table => new
                {
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExperimentKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FirstExposureMs = table.Column<long>(type: "bigint", nullable: false),
                    Conflicted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_experiment_user_states", x => new { x.EnvironmentId, x.ExperimentKey, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "metric_user_states",
                columns: table => new
                {
                    EnvironmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Metric = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastConversionMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metric_user_states", x => new { x.EnvironmentId, x.Metric, x.UserId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "experiment_user_states");

            migrationBuilder.DropTable(
                name: "metric_user_states");
        }
    }
}
