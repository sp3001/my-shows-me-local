using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shows.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0008_UserShowUserRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "user_rating",
                table: "user_show",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "user_rating",
                table: "user_show");
        }
    }
}
