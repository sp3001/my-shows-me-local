using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Shows.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0011_ShowStatusTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "status_changed_at",
                table: "tracked_show",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "status_renewed_at",
                table: "tracked_show",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "tvmaze_updated_at",
                table: "tracked_show",
                type: "TEXT",
                nullable: true);

            // ---- backfill (ADR-038) ----
            //
            // status_changed_at: a terminal show's end date is its last aired episode, NOT the moment we
            // happened to learn it — the same rule the mapper applies on a first map, so imported rows and
            // newly-mapped rows agree. air_stamp is copied verbatim rather than reformatted: it is already
            // a DateTimeOffset in the provider's own TEXT form, so the value round-trips unchanged.
            //
            // max() over TEXT is lexicographic, which equals chronological here: every stored air_stamp is
            // the same 25-char shape with the same zero offset (verified against the live DB — 19,503 rows,
            // one distinct suffix length, all "+00:00"). A show with no dated episode is left NULL and the
            // mapper fills it in on its next map, which is why the mapper's rule tolerates a NULL on a
            // terminal row.
            //
            // status_renewed_at is deliberately NOT backfilled. A renewal is an observed transition, and
            // nothing observed one before now — inventing a date would put every show into "Renewed".
            migrationBuilder.Sql(
                """
                UPDATE tracked_show
                SET status_changed_at = (
                    SELECT max(e.air_stamp) FROM episode e
                    WHERE e.show_id = tracked_show.id AND e.air_stamp IS NOT NULL)
                WHERE status_id IN (2, 3)
                  AND status_changed_at IS NULL
                  AND (SELECT max(e.air_stamp) FROM episode e
                       WHERE e.show_id = tracked_show.id AND e.air_stamp IS NOT NULL) IS NOT NULL
                """);

            // tvmaze_updated_at: the mapped copy of the source column, unix seconds -> the same 25-char
            // UTC TEXT shape the provider writes. strftime's output has no fractional part, which is
            // correct here — provider_updated is whole seconds to begin with.
            migrationBuilder.Sql(
                """
                UPDATE tracked_show
                SET tvmaze_updated_at = (
                    SELECT strftime('%Y-%m-%d %H:%M:%S+00:00', s.provider_updated, 'unixepoch')
                    FROM tvmaze_show s WHERE s.tvmaze_id = tracked_show.tvmaze_id)
                WHERE tvmaze_updated_at IS NULL
                  AND (SELECT s.provider_updated FROM tvmaze_show s
                       WHERE s.tvmaze_id = tracked_show.tvmaze_id) IS NOT NULL
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "status_changed_at",
                table: "tracked_show");

            migrationBuilder.DropColumn(
                name: "status_renewed_at",
                table: "tracked_show");

            migrationBuilder.DropColumn(
                name: "tvmaze_updated_at",
                table: "tracked_show");
        }
    }
}
