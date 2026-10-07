using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shows.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0002_SourceSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "tvmaze_show_id",
                table: "tracked_show",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "imdb_akas",
                columns: table => new
                {
                    title_id = table.Column<string>(type: "TEXT", nullable: false),
                    ordering = table.Column<int>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    region = table.Column<string>(type: "TEXT", nullable: true),
                    language = table.Column<string>(type: "TEXT", nullable: true),
                    is_original_title = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imdb_akas", x => new { x.title_id, x.ordering });
                });

            migrationBuilder.CreateTable(
                name: "imdb_dataset_meta",
                columns: table => new
                {
                    dataset_name = table.Column<string>(type: "TEXT", nullable: false),
                    downloaded_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    source_url = table.Column<string>(type: "TEXT", nullable: false),
                    row_count = table.Column<long>(type: "INTEGER", nullable: true),
                    file_size = table.Column<long>(type: "INTEGER", nullable: true),
                    checksum = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imdb_dataset_meta", x => x.dataset_name);
                });

            migrationBuilder.CreateTable(
                name: "imdb_title",
                columns: table => new
                {
                    tconst = table.Column<string>(type: "TEXT", nullable: false),
                    title_type = table.Column<string>(type: "TEXT", nullable: false),
                    primary_title = table.Column<string>(type: "TEXT", nullable: false),
                    original_title = table.Column<string>(type: "TEXT", nullable: true),
                    is_adult = table.Column<bool>(type: "INTEGER", nullable: false),
                    start_year = table.Column<int>(type: "INTEGER", nullable: true),
                    end_year = table.Column<int>(type: "INTEGER", nullable: true),
                    runtime_minutes = table.Column<int>(type: "INTEGER", nullable: true),
                    genres = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imdb_title", x => x.tconst);
                });

            migrationBuilder.CreateTable(
                name: "tvmaze_show",
                columns: table => new
                {
                    tvmaze_id = table.Column<int>(type: "INTEGER", nullable: false),
                    url = table.Column<string>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    type_raw = table.Column<string>(type: "TEXT", nullable: true),
                    language = table.Column<string>(type: "TEXT", nullable: true),
                    status_raw = table.Column<string>(type: "TEXT", nullable: false),
                    runtime_minutes = table.Column<int>(type: "INTEGER", nullable: true),
                    average_runtime = table.Column<int>(type: "INTEGER", nullable: true),
                    premiered = table.Column<string>(type: "TEXT", nullable: true),
                    ended = table.Column<string>(type: "TEXT", nullable: true),
                    official_site = table.Column<string>(type: "TEXT", nullable: true),
                    schedule_time = table.Column<string>(type: "TEXT", nullable: true),
                    schedule_days = table.Column<string>(type: "TEXT", nullable: true),
                    rating_average = table.Column<double>(type: "REAL", nullable: true),
                    network_name = table.Column<string>(type: "TEXT", nullable: true),
                    webchannel_name = table.Column<string>(type: "TEXT", nullable: true),
                    imdb_id = table.Column<string>(type: "TEXT", nullable: true),
                    thetvdb_id = table.Column<int>(type: "INTEGER", nullable: true),
                    tvrage_id = table.Column<int>(type: "INTEGER", nullable: true),
                    genres_raw = table.Column<string>(type: "TEXT", nullable: true),
                    image_original = table.Column<string>(type: "TEXT", nullable: true),
                    image_medium = table.Column<string>(type: "TEXT", nullable: true),
                    summary_html = table.Column<string>(type: "TEXT", nullable: true),
                    provider_updated = table.Column<long>(type: "INTEGER", nullable: true),
                    raw_json = table.Column<string>(type: "TEXT", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tvmaze_show", x => x.tvmaze_id);
                });

            migrationBuilder.CreateTable(
                name: "tvmaze_status_seen",
                columns: table => new
                {
                    status_raw = table.Column<string>(type: "TEXT", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    occurrences = table.Column<int>(type: "INTEGER", nullable: false),
                    mapped_to = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tvmaze_status_seen", x => x.status_raw);
                });

            migrationBuilder.CreateTable(
                name: "imdb_episode",
                columns: table => new
                {
                    tconst = table.Column<string>(type: "TEXT", nullable: false),
                    parent_tconst = table.Column<string>(type: "TEXT", nullable: false),
                    season_number = table.Column<int>(type: "INTEGER", nullable: true),
                    episode_number = table.Column<int>(type: "INTEGER", nullable: true),
                    primary_title = table.Column<string>(type: "TEXT", nullable: true),
                    start_year = table.Column<int>(type: "INTEGER", nullable: true),
                    runtime_minutes = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imdb_episode", x => x.tconst);
                    table.ForeignKey(
                        name: "fk_imdb_episode_imdb_title_parent_tconst",
                        column: x => x.parent_tconst,
                        principalTable: "imdb_title",
                        principalColumn: "tconst",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tvmaze_episode",
                columns: table => new
                {
                    tvmaze_episode_id = table.Column<int>(type: "INTEGER", nullable: false),
                    tvmaze_show_id = table.Column<int>(type: "INTEGER", nullable: false),
                    url = table.Column<string>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    season_number = table.Column<int>(type: "INTEGER", nullable: false),
                    number = table.Column<int>(type: "INTEGER", nullable: true),
                    type_raw = table.Column<string>(type: "TEXT", nullable: false),
                    airdate = table.Column<string>(type: "TEXT", nullable: true),
                    airstamp = table.Column<string>(type: "TEXT", nullable: true),
                    runtime_minutes = table.Column<int>(type: "INTEGER", nullable: true),
                    rating_average = table.Column<double>(type: "REAL", nullable: true),
                    image_original = table.Column<string>(type: "TEXT", nullable: true),
                    image_medium = table.Column<string>(type: "TEXT", nullable: true),
                    summary_html = table.Column<string>(type: "TEXT", nullable: true),
                    raw_json = table.Column<string>(type: "TEXT", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tvmaze_episode", x => x.tvmaze_episode_id);
                    table.ForeignKey(
                        name: "fk_tvmaze_episode_tvmaze_show_tvmaze_show_id",
                        column: x => x.tvmaze_show_id,
                        principalTable: "tvmaze_show",
                        principalColumn: "tvmaze_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tracked_show_tvmaze_show_id",
                table: "tracked_show",
                column: "tvmaze_show_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_imdb_ep_parent",
                table: "imdb_episode",
                columns: new[] { "parent_tconst", "season_number", "episode_number" });

            migrationBuilder.CreateIndex(
                name: "ix_tvmaze_ep_show",
                table: "tvmaze_episode",
                columns: new[] { "tvmaze_show_id", "season_number", "number" });

            migrationBuilder.AddForeignKey(
                name: "fk_tracked_show_tvmaze_show_tvmaze_show_id",
                table: "tracked_show",
                column: "tvmaze_show_id",
                principalTable: "tvmaze_show",
                principalColumn: "tvmaze_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tracked_show_tvmaze_show_tvmaze_show_id",
                table: "tracked_show");

            migrationBuilder.DropTable(
                name: "imdb_akas");

            migrationBuilder.DropTable(
                name: "imdb_dataset_meta");

            migrationBuilder.DropTable(
                name: "imdb_episode");

            migrationBuilder.DropTable(
                name: "tvmaze_episode");

            migrationBuilder.DropTable(
                name: "tvmaze_status_seen");

            migrationBuilder.DropTable(
                name: "imdb_title");

            migrationBuilder.DropTable(
                name: "tvmaze_show");

            migrationBuilder.DropIndex(
                name: "ix_tracked_show_tvmaze_show_id",
                table: "tracked_show");

            migrationBuilder.DropColumn(
                name: "tvmaze_show_id",
                table: "tracked_show");
        }
    }
}
