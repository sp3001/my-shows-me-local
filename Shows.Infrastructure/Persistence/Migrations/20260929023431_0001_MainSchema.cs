using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shows.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0001_MainSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "genre",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_genre", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "http_cache",
                columns: table => new
                {
                    url = table.Column<string>(type: "TEXT", nullable: false),
                    etag = table.Column<string>(type: "TEXT", nullable: true),
                    last_modified = table.Column<string>(type: "TEXT", nullable: true),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_http_cache", x => x.url);
                });

            migrationBuilder.CreateTable(
                name: "job_run",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    job_name = table.Column<string>(type: "TEXT", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    items_total = table.Column<int>(type: "INTEGER", nullable: true),
                    items_changed = table.Column<int>(type: "INTEGER", nullable: true),
                    detail = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tracked_show",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    tvmaze_id = table.Column<int>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    title_localized = table.Column<string>(type: "TEXT", nullable: true),
                    show_type = table.Column<string>(type: "TEXT", nullable: true),
                    language = table.Column<string>(type: "TEXT", nullable: true),
                    status_id = table.Column<int>(type: "INTEGER", nullable: false),
                    runtime_minutes = table.Column<int>(type: "INTEGER", nullable: true),
                    average_runtime_minutes = table.Column<int>(type: "INTEGER", nullable: true),
                    premiere_date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    end_date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    official_site = table.Column<string>(type: "TEXT", nullable: true),
                    schedule_time = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    schedule_days = table.Column<string>(type: "TEXT", nullable: true),
                    rating_average = table.Column<double>(type: "REAL", nullable: true),
                    network_name = table.Column<string>(type: "TEXT", nullable: true),
                    webchannel_name = table.Column<string>(type: "TEXT", nullable: true),
                    imdb_id = table.Column<string>(type: "TEXT", nullable: true),
                    the_tv_db_id = table.Column<int>(type: "INTEGER", nullable: true),
                    poster_url = table.Column<string>(type: "TEXT", nullable: true),
                    poster_medium_url = table.Column<string>(type: "TEXT", nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    mapped_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    watched_regular = table.Column<int>(type: "INTEGER", nullable: false),
                    total_regular = table.Column<int>(type: "INTEGER", nullable: false),
                    has_unwatched_specials = table.Column<bool>(type: "INTEGER", nullable: false),
                    auto_finish_eligible = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tracked_show", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "writer_lease",
                columns: table => new
                {
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    holder = table.Column<string>(type: "TEXT", nullable: false),
                    acquired_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_writer_lease", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "season",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    show_id = table.Column<long>(type: "INTEGER", nullable: false),
                    season_number = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: true),
                    episode_count = table.Column<int>(type: "INTEGER", nullable: true),
                    poster_url = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_season", x => x.id);
                    table.ForeignKey(
                        name: "fk_season_tracked_show_show_id",
                        column: x => x.show_id,
                        principalTable: "tracked_show",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "show_genre",
                columns: table => new
                {
                    show_id = table.Column<long>(type: "INTEGER", nullable: false),
                    genre_id = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_show_genre", x => new { x.show_id, x.genre_id });
                    table.ForeignKey(
                        name: "fk_show_genre_genre_genre_id",
                        column: x => x.genre_id,
                        principalTable: "genre",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_show_genre_tracked_show_show_id",
                        column: x => x.show_id,
                        principalTable: "tracked_show",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_show",
                columns: table => new
                {
                    tracked_show_id = table.Column<long>(type: "INTEGER", nullable: false),
                    category_id = table.Column<int>(type: "INTEGER", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    category_changed_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    needs_review = table.Column<bool>(type: "INTEGER", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_show", x => x.tracked_show_id);
                    table.ForeignKey(
                        name: "fk_user_show_tracked_show_tracked_show_id",
                        column: x => x.tracked_show_id,
                        principalTable: "tracked_show",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "episode",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    show_id = table.Column<long>(type: "INTEGER", nullable: false),
                    season_id = table.Column<long>(type: "INTEGER", nullable: false),
                    tvmaze_episode_id = table.Column<int>(type: "INTEGER", nullable: true),
                    season_number = table.Column<int>(type: "INTEGER", nullable: false),
                    episode_number = table.Column<int>(type: "INTEGER", nullable: true),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    title_localized = table.Column<string>(type: "TEXT", nullable: true),
                    episode_type_id = table.Column<int>(type: "INTEGER", nullable: false),
                    air_date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    air_stamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    runtime_minutes = table.Column<int>(type: "INTEGER", nullable: true),
                    rating_average = table.Column<double>(type: "REAL", nullable: true),
                    image_url = table.Column<string>(type: "TEXT", nullable: true),
                    image_medium_url = table.Column<string>(type: "TEXT", nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    imdb_id = table.Column<string>(type: "TEXT", nullable: true),
                    title_source = table.Column<string>(type: "TEXT", nullable: true),
                    air_date_source = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_episode", x => x.id);
                    table.ForeignKey(
                        name: "fk_episode_season_season_id",
                        column: x => x.season_id,
                        principalTable: "season",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_episode_tracked_show_show_id",
                        column: x => x.show_id,
                        principalTable: "tracked_show",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ui_season_state",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    show_id = table.Column<long>(type: "INTEGER", nullable: false),
                    season_id = table.Column<long>(type: "INTEGER", nullable: false),
                    is_expanded = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ui_season_state", x => x.id);
                    table.ForeignKey(
                        name: "fk_ui_season_state_season_season_id",
                        column: x => x.season_id,
                        principalTable: "season",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ui_season_state_tracked_show_show_id",
                        column: x => x.show_id,
                        principalTable: "tracked_show",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "episode_watch",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    show_id = table.Column<long>(type: "INTEGER", nullable: false),
                    episode_id = table.Column<long>(type: "INTEGER", nullable: false),
                    watched_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_episode_watch", x => x.id);
                    table.ForeignKey(
                        name: "fk_episode_watch_episode_episode_id",
                        column: x => x.episode_id,
                        principalTable: "episode",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_episode_watch_tracked_show_show_id",
                        column: x => x.show_id,
                        principalTable: "tracked_show",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_episode_airdate",
                table: "episode",
                column: "air_date",
                filter: "air_date IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_episode_imdb",
                table: "episode",
                column: "imdb_id",
                filter: "imdb_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_episode_key",
                table: "episode",
                columns: new[] { "show_id", "season_number", "episode_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_episode_season_id",
                table: "episode",
                column: "season_id");

            migrationBuilder.CreateIndex(
                name: "ix_episode_tvmaze",
                table: "episode",
                column: "tvmaze_episode_id",
                unique: true,
                filter: "tvmaze_episode_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_episode_watch_episode_id",
                table: "episode_watch",
                column: "episode_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_watch_show",
                table: "episode_watch",
                column: "show_id");

            migrationBuilder.CreateIndex(
                name: "ix_genre_name",
                table: "genre",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_job_run_name",
                table: "job_run",
                columns: new[] { "job_name", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_season_show_num",
                table: "season",
                columns: new[] { "show_id", "season_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_show_genre_genre_id",
                table: "show_genre",
                column: "genre_id");

            migrationBuilder.CreateIndex(
                name: "ix_tracked_show_imdb_id",
                table: "tracked_show",
                column: "imdb_id",
                unique: true,
                filter: "imdb_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tracked_show_tvmaze_id",
                table: "tracked_show",
                column: "tvmaze_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ui_season_state_season_id",
                table: "ui_season_state",
                column: "season_id");

            migrationBuilder.CreateIndex(
                name: "ix_ui_season_state_show_id_season_id",
                table: "ui_season_state",
                columns: new[] { "show_id", "season_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ui_state_show",
                table: "ui_season_state",
                column: "show_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_show_category",
                table: "user_show",
                columns: new[] { "category_id", "deleted_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "episode_watch");

            migrationBuilder.DropTable(
                name: "http_cache");

            migrationBuilder.DropTable(
                name: "job_run");

            migrationBuilder.DropTable(
                name: "show_genre");

            migrationBuilder.DropTable(
                name: "ui_season_state");

            migrationBuilder.DropTable(
                name: "user_show");

            migrationBuilder.DropTable(
                name: "writer_lease");

            migrationBuilder.DropTable(
                name: "episode");

            migrationBuilder.DropTable(
                name: "genre");

            migrationBuilder.DropTable(
                name: "season");

            migrationBuilder.DropTable(
                name: "tracked_show");
        }
    }
}
