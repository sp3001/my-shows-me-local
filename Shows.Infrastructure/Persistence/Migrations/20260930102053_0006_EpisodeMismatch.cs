using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shows.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0006_EpisodeMismatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "episode_mismatch",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    show_id = table.Column<long>(type: "INTEGER", nullable: false),
                    episode_id = table.Column<long>(type: "INTEGER", nullable: true),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    tvmaze_value = table.Column<string>(type: "TEXT", nullable: true),
                    imdb_value = table.Column<string>(type: "TEXT", nullable: true),
                    season_number = table.Column<int>(type: "INTEGER", nullable: true),
                    episode_number = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_episode_mismatch", x => x.id);
                    table.ForeignKey(
                        name: "fk_episode_mismatch_episode_episode_id",
                        column: x => x.episode_id,
                        principalTable: "episode",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_episode_mismatch_tracked_show_show_id",
                        column: x => x.show_id,
                        principalTable: "tracked_show",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_episode_mismatch_episode_id",
                table: "episode_mismatch",
                column: "episode_id");

            migrationBuilder.CreateIndex(
                name: "ix_mismatch_show",
                table: "episode_mismatch",
                column: "show_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "episode_mismatch");
        }
    }
}
