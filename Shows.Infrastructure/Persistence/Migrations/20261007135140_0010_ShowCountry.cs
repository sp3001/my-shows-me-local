using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shows.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0010_ShowCountry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "country_code",
                table: "tvmaze_show",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "country_name",
                table: "tvmaze_show",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "country",
                table: "tracked_show",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "country_code",
                table: "tvmaze_show");

            migrationBuilder.DropColumn(
                name: "country_name",
                table: "tvmaze_show");

            migrationBuilder.DropColumn(
                name: "country",
                table: "tracked_show");
        }
    }
}
