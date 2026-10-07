using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shows.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0005_TvmazeAkas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tvmaze_akas",
                columns: table => new
                {
                    tvmaze_show_id = table.Column<int>(type: "INTEGER", nullable: false),
                    country_code = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    country_name = table.Column<string>(type: "TEXT", nullable: true),
                    aka_order = table.Column<int>(type: "INTEGER", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tvmaze_akas", x => new { x.tvmaze_show_id, x.country_code, x.name });
                    table.ForeignKey(
                        name: "fk_tvmaze_akas_tvmaze_show_tvmaze_show_id",
                        column: x => x.tvmaze_show_id,
                        principalTable: "tvmaze_show",
                        principalColumn: "tvmaze_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tvmaze_akas_pick",
                table: "tvmaze_akas",
                columns: new[] { "tvmaze_show_id", "country_code", "aka_order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tvmaze_akas");
        }
    }
}
