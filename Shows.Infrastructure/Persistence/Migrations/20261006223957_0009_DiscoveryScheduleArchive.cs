using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shows.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0009_DiscoveryScheduleArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discovery_schedule_archive",
                columns: table => new
                {
                    fetched_on = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    raw_json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discovery_schedule_archive", x => x.fetched_on);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discovery_schedule_archive");
        }
    }
}
