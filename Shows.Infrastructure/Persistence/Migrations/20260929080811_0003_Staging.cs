using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shows.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0003_Staging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "staging_episode",
                columns: table => new
                {
                    show_myshows_id = table.Column<int>(type: "INTEGER", nullable: false),
                    season_number = table.Column<int>(type: "INTEGER", nullable: false),
                    episode_number = table.Column<int>(type: "INTEGER", nullable: false),
                    source_batch_id = table.Column<int>(type: "INTEGER", nullable: false),
                    episode_id_src = table.Column<int>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    air_date_utc = table.Column<string>(type: "TEXT", nullable: true),
                    is_special = table.Column<bool>(type: "INTEGER", nullable: false),
                    runtime_minutes = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staging_episode", x => new { x.show_myshows_id, x.season_number, x.episode_number });
                });

            migrationBuilder.CreateTable(
                name: "staging_imdb_id",
                columns: table => new
                {
                    myshows_id = table.Column<int>(type: "INTEGER", nullable: false),
                    imdb_id = table.Column<string>(type: "TEXT", nullable: true),
                    kinopoisk_id = table.Column<string>(type: "TEXT", nullable: true),
                    scraped_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    http_status = table.Column<int>(type: "INTEGER", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staging_imdb_id", x => x.myshows_id);
                });

            migrationBuilder.CreateTable(
                name: "staging_show",
                columns: table => new
                {
                    myshows_id = table.Column<int>(type: "INTEGER", nullable: false),
                    title_ru = table.Column<string>(type: "TEXT", nullable: true),
                    title_original = table.Column<string>(type: "TEXT", nullable: true),
                    year = table.Column<int>(type: "INTEGER", nullable: true),
                    alias = table.Column<string>(type: "TEXT", nullable: true),
                    source_file = table.Column<string>(type: "TEXT", nullable: false),
                    source_watch_status = table.Column<string>(type: "TEXT", nullable: false),
                    total_episodes = table.Column<int>(type: "INTEGER", nullable: false),
                    watched_episodes = table.Column<int>(type: "INTEGER", nullable: false),
                    total_seasons = table.Column<int>(type: "INTEGER", nullable: true),
                    imdb_id = table.Column<string>(type: "TEXT", nullable: true),
                    tvmaze_id = table.Column<int>(type: "INTEGER", nullable: true),
                    tvmaze_resolution = table.Column<string>(type: "TEXT", nullable: false),
                    needs_review = table.Column<bool>(type: "INTEGER", nullable: false),
                    promoted_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staging_show", x => x.myshows_id);
                });

            // v_staging_issues — the promote gate (SCHEMA.md §6). Hand-written: views are not
            // EF-first-class, and the CASE logic is the gate's whole contract. It joins imdb_episode
            // and tvmaze_episode (created in 0002), so it must run after that migration.
            migrationBuilder.Sql(ViewSql);
        }

        // SCHEMA.md §6, verbatim. The TVmaze count comes from tvmaze_episode, which is only populated
        // once a show's episodes have been fetched — so tvmaze_count_mismatch is silent until episode
        // fetch, and the view is only fully meaningful after step 2.3 plus an episode fetch.
        private const string ViewSql = """
            CREATE VIEW v_staging_issues AS
            SELECT s.myshows_id,
                   s.title_ru,
                   CASE
                     WHEN s.tvmaze_id IS NULL
                       THEN 'unresolved_tvmaze_id'
                     WHEN s.imdb_id IS NULL
                       THEN 'unresolved_imdb_id'
                     WHEN s.source_watch_status = 'watching'
                          AND s.watched_episodes > s.total_episodes
                       THEN 'watched_exceeds_total'
                     WHEN s.total_episodes = 0
                       THEN 'zero_total_episodes'
                     WHEN s.total_episodes <> COALESCE(imdb.cnt, s.total_episodes)
                       THEN 'imdb_count_mismatch'
                     WHEN s.total_episodes <> COALESCE(tvm.cnt, s.total_episodes)
                       THEN 'tvmaze_count_mismatch'
                   END AS issue,
                   CASE
                     WHEN s.tvmaze_id IS NULL THEN 'blocking'
                     WHEN s.imdb_id IS NULL THEN 'blocking'
                     WHEN s.watched_episodes > s.total_episodes THEN 'blocking'
                     ELSE 'warning'
                   END AS severity
            FROM staging_show s
            LEFT JOIN (SELECT parent_tconst p, COUNT(*) cnt
                       FROM imdb_episode
                       WHERE season_number IS NOT NULL
                       GROUP BY parent_tconst) imdb ON imdb.p = s.imdb_id
            LEFT JOIN (SELECT tvmaze_show_id s, COUNT(*) cnt
                       FROM tvmaze_episode
                       GROUP BY tvmaze_show_id) tvm ON tvm.s = s.tvmaze_id
            WHERE issue IS NOT NULL;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS v_staging_issues;");

            migrationBuilder.DropTable(
                name: "staging_episode");

            migrationBuilder.DropTable(
                name: "staging_imdb_id");

            migrationBuilder.DropTable(
                name: "staging_show");
        }
    }
}
