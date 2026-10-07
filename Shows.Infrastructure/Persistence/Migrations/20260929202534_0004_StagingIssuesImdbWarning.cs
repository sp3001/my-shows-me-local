using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shows.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0004_StagingIssuesImdbWarning : Migration
    {
        // ADR-030: a show that has a tvmaze_id is never BLOCKING for promotion just because it
        // lacks an imdb_id — IMDb is enrichment, not a promotion prerequisite (the mapper needs only
        // tvmaze_id). The 'unresolved_imdb_id' row remains as a WARNING so the reviewer still sees
        // it. This is the migration-0003 view with exactly ONE line removed: the
        // "WHEN s.imdb_id IS NULL THEN 'blocking'" branch of the severity CASE. Everything else —
        // the issue CASE, the tri-source JOINs, the WHERE — is byte-for-byte the 0003 view.
        private const string NewViewSql = """
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

        // The pre-ADR-030 view (migration 0003), restored verbatim on rollback.
        private const string OriginalViewSql = """
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
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS v_staging_issues;");
            migrationBuilder.Sql(NewViewSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS v_staging_issues;");
            migrationBuilder.Sql(OriginalViewSql);
        }
    }
}
