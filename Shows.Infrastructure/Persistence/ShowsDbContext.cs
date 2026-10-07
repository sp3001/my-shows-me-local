using Microsoft.EntityFrameworkCore;
using Shows.Domain;
using Shows.Infrastructure.Persistence.Sources;
using Shows.Infrastructure.Persistence.Staging;

namespace Shows.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context over SQLite (ARCHITECTURE.md §3). Owns the mapped, user, and
/// bookkeeping tables; the source and staging tables arrive in steps 1.2b and 1.3.
/// </summary>
/// <remarks>
/// <para>
/// Two conventions are load-bearing and easy to get silently wrong (SCHEMA.md §1a): snake_case naming,
/// and <see cref="ValueGenerated.Never"/> on <em>every</em> property so the SQLite provider never
/// assumes an <c>INTEGER PRIMARY KEY</c> is a store-generated rowid. The latter is applied in
/// <see cref="ApplyNeverValueGeneration"/> as one sweep over the model, so no entity can opt out by
/// forgetting — and it is asserted reflectively in the tests, because the model (not the migration)
/// is what decides whether EF tries to read a generated value back.
/// </para>
/// <para>
/// <c>tracked_show.tvmaze_show_id</c> (the FK to <c>tvmaze_show</c>) is intentionally absent here: its
/// target table is created in migration 0002 (step 1.2b). Adding the FK now would emit it into 0001
/// against a table that does not exist yet. Only <c>tvmaze_id</c> (the denormalized, readable copy) is
/// mapped in 0001; the FK column is introduced with its target in 1.2b.
/// </para>
/// </remarks>
public class ShowsDbContext(DbContextOptions<ShowsDbContext> options) : DbContext(options)
{
    public DbSet<TrackedShow> TrackedShows => Set<TrackedShow>();
    public DbSet<UserShow> UserShows => Set<UserShow>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<EpisodeMismatch> EpisodeMismatches => Set<EpisodeMismatch>();
    public DbSet<EpisodeWatch> EpisodeWatches => Set<EpisodeWatch>();
    public DbSet<Genre> Genres => Set<Genre>();
    public DbSet<UiSeasonState> UiSeasonStates => Set<UiSeasonState>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<WriterLease> WriterLeases => Set<WriterLease>();
    public DbSet<HttpCacheEntry> HttpCache => Set<HttpCacheEntry>();
    public DbSet<DiscoveryScheduleArchiveEntry> DiscoveryScheduleArchives => Set<DiscoveryScheduleArchiveEntry>();

    // Source tables (SCHEMA.md §3–§4). Read by the mapper and nothing else (SPEC P3/R11).
    public DbSet<TvmazeShow> TvmazeShows => Set<TvmazeShow>();
    public DbSet<TvmazeEpisode> TvmazeEpisodes => Set<TvmazeEpisode>();
    public DbSet<TvmazeStatusSeen> TvmazeStatusSeen => Set<TvmazeStatusSeen>();
    public DbSet<TvmazeAka> TvmazeAkas => Set<TvmazeAka>();
    public DbSet<ImdbTitle> ImdbTitles => Set<ImdbTitle>();
    public DbSet<ImdbEpisode> ImdbEpisodes => Set<ImdbEpisode>();
    public DbSet<ImdbAkas> ImdbAkas => Set<ImdbAkas>();
    public DbSet<ImdbDatasetMeta> ImdbDatasetMeta => Set<ImdbDatasetMeta>();

    // Staging tables (SCHEMA.md §5) — the myshows source layer. Application never references them.
    public DbSet<StagingShow> StagingShows => Set<StagingShow>();
    public DbSet<StagingEpisode> StagingEpisodes => Set<StagingEpisode>();
    public DbSet<StagingImdbId> StagingImdbIds => Set<StagingImdbId>();

    /// <summary>The promote gate (SCHEMA.md §6). Read-only view; never add or remove.</summary>
    public DbSet<StagingIssue> StagingIssues => Set<StagingIssue>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ---- tracked_show / user_show (1:1, shared PK — ADR-024) ----
        var show = modelBuilder.Entity<TrackedShow>();
        show.ToTable("tracked_show");
        show.HasKey(s => s.Id);
        show.Property(s => s.Status).HasColumnName("status_id");
        show.Property(s => s.HasUnwatchedSpecials).HasColumnName("has_unwatched_specials");
        show.Property(s => s.AutoFinishEligible).HasColumnName("auto_finish_eligible");
        show.HasIndex(s => s.TvmazeId).IsUnique();
        show.HasIndex(s => s.ImdbId).IsUnique().HasFilter("imdb_id IS NOT NULL");

        var user = modelBuilder.Entity<UserShow>();
        user.ToTable("user_show");
        user.HasKey(u => u.TrackedShowId);
        user.Property(u => u.Category).HasColumnName("category_id");
        user.Property(u => u.NeedsReview).HasColumnName("needs_review");
        user.Property(u => u.IsPinned).HasColumnName("is_pinned");
        user.Property(u => u.UserRating).HasColumnName("user_rating");
        // Shared primary key: user_show.tracked_show_id is both PK and FK to tracked_show.id.
        user.HasOne(u => u.Show).WithOne(s => s.User!)
            .HasForeignKey<UserShow>(u => u.TrackedShowId)
            .OnDelete(DeleteBehavior.Cascade);
        user.HasIndex(u => new { u.Category, u.DeletedAt }).HasDatabaseName("ix_user_show_category");

        // ---- season ----
        var season = modelBuilder.Entity<Season>();
        season.ToTable("season");
        season.HasKey(s => s.Id);
        season.HasOne<TrackedShow>().WithMany(s => s.Seasons).HasForeignKey(s => s.ShowId)
            .OnDelete(DeleteBehavior.Cascade);
        season.HasIndex(s => new { s.ShowId, s.SeasonNumber }).IsUnique()
            .HasDatabaseName("ix_season_show_num");

        // ---- episode ----
        var episode = modelBuilder.Entity<Episode>();
        episode.ToTable("episode");
        episode.HasKey(e => e.Id);
        episode.Property(e => e.EpisodeType).HasColumnName("episode_type_id");
        episode.HasOne<TrackedShow>().WithMany(s => s.Episodes).HasForeignKey(e => e.ShowId)
            .OnDelete(DeleteBehavior.Cascade);
        episode.HasOne<Season>().WithMany(s => s.Episodes).HasForeignKey(e => e.SeasonId)
            .OnDelete(DeleteBehavior.Restrict);
        episode.HasIndex(e => new { e.ShowId, e.SeasonNumber, e.EpisodeNumber }).IsUnique()
            .HasDatabaseName("ix_episode_key");
        episode.HasIndex(e => e.AirDate).HasDatabaseName("ix_episode_airdate")
            .HasFilter("air_date IS NOT NULL");
        episode.HasIndex(e => e.ImdbId).HasDatabaseName("ix_episode_imdb")
            .HasFilter("imdb_id IS NOT NULL");
        episode.HasIndex(e => e.TvmazeEpisodeId).IsUnique().HasDatabaseName("ix_episode_tvmaze")
            .HasFilter("tvmaze_episode_id IS NOT NULL");

        // ---- episode_watch (user-owned) ----
        var watch = modelBuilder.Entity<EpisodeWatch>();
        watch.ToTable("episode_watch");
        watch.HasKey(w => w.Id);
        watch.HasOne<TrackedShow>().WithMany().HasForeignKey(w => w.ShowId)
            .OnDelete(DeleteBehavior.Cascade);
        watch.HasOne<Episode>().WithMany().HasForeignKey(w => w.EpisodeId)
            .OnDelete(DeleteBehavior.Cascade);
        watch.HasIndex(w => w.EpisodeId).IsUnique();
        watch.HasIndex(w => w.ShowId).HasDatabaseName("ix_watch_show");

        // ---- episode_mismatch (derived/rebuildable — SCHEMA §4a, ADR-031) ----
        // Wiped and recomputed per show by the enrichment mode, so no unique constraint is needed (the
        // wipe is the idempotence mechanism). Cascade from both parents: a mismatch row is about a
        // specific episode/show and must not outlive them. episode_id is NULL for an imdb_only row.
        var mismatch = modelBuilder.Entity<EpisodeMismatch>();
        mismatch.ToTable("episode_mismatch");
        mismatch.HasKey(m => m.Id);
        mismatch.Property(m => m.Kind).HasColumnName("kind");
        mismatch.HasOne<TrackedShow>().WithMany().HasForeignKey(m => m.ShowId)
            .OnDelete(DeleteBehavior.Cascade);
        mismatch.HasOne<Episode>().WithMany().HasForeignKey(m => m.EpisodeId)
            .OnDelete(DeleteBehavior.Cascade);
        mismatch.HasIndex(m => m.ShowId).HasDatabaseName("ix_mismatch_show");

        // ---- genre / show_genre (many-to-many) ----
        modelBuilder.Entity<Genre>().ToTable("genre");
        modelBuilder.Entity<Genre>().HasKey(g => g.Id);
        modelBuilder.Entity<Genre>().HasIndex(g => g.Name).IsUnique();
        modelBuilder.Entity<TrackedShow>()
            .HasMany(s => s.Genres)
            .WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "show_genre",
                j => j.HasOne<Genre>().WithMany().HasForeignKey("genre_id").OnDelete(DeleteBehavior.Cascade),
                j => j.HasOne<TrackedShow>().WithMany().HasForeignKey("show_id").OnDelete(DeleteBehavior.Cascade),
                j => j.HasKey("show_id", "genre_id"));

        // ---- ui_season_state (user-owned) ----
        var ui = modelBuilder.Entity<UiSeasonState>();
        ui.ToTable("ui_season_state");
        ui.HasKey(u => u.Id);
        ui.Property(u => u.IsExpanded).HasColumnName("is_expanded");
        ui.HasOne<TrackedShow>().WithMany().HasForeignKey(u => u.ShowId)
            .OnDelete(DeleteBehavior.Cascade);
        ui.HasOne<Season>().WithMany().HasForeignKey(u => u.SeasonId)
            .OnDelete(DeleteBehavior.Cascade);
        ui.HasIndex(u => new { u.ShowId, u.SeasonId }).IsUnique();
        ui.HasIndex(u => u.ShowId).HasDatabaseName("ix_ui_state_show");

        // ---- bookkeeping ----
        modelBuilder.Entity<JobRun>().ToTable("job_run");
        modelBuilder.Entity<JobRun>().HasKey(j => j.Id);
        modelBuilder.Entity<JobRun>().HasIndex(j => new { j.JobName, j.StartedAt })
            .IsDescending(false, true).HasDatabaseName("ix_job_run_name");

        modelBuilder.Entity<WriterLease>().ToTable("writer_lease");
        modelBuilder.Entity<WriterLease>().HasKey(w => w.Name);

        modelBuilder.Entity<HttpCacheEntry>().ToTable("http_cache");
        modelBuilder.Entity<HttpCacheEntry>().HasKey(h => h.Url);

        modelBuilder.Entity<DiscoveryScheduleArchiveEntry>(archive =>
        {
            archive.ToTable("discovery_schedule_archive");
            archive.HasKey(entry => entry.FetchedOn);
            archive.Property(entry => entry.FetchedOn).HasColumnType("TEXT");
            archive.Property(entry => entry.RawJson).IsRequired();
        });

        // ---- source tables (SCHEMA.md §3–§4) — provider field names verbatim, mapper-only ----
        var tvmazeShow = modelBuilder.Entity<TvmazeShow>();
        tvmazeShow.ToTable("tvmaze_show");
        tvmazeShow.HasKey(t => t.TvmazeId);

        var tvmazeEpisode = modelBuilder.Entity<TvmazeEpisode>();
        tvmazeEpisode.ToTable("tvmaze_episode");
        tvmazeEpisode.HasKey(e => e.TvmazeEpisodeId);
        tvmazeEpisode.HasOne<TvmazeShow>().WithMany().HasForeignKey(e => e.TvmazeShowId)
            .OnDelete(DeleteBehavior.Cascade);
        tvmazeEpisode.HasIndex(e => new { e.TvmazeShowId, e.SeasonNumber, e.Number })
            .HasDatabaseName("ix_tvmaze_ep_show");

        modelBuilder.Entity<TvmazeStatusSeen>().ToTable("tvmaze_status_seen");
        modelBuilder.Entity<TvmazeStatusSeen>().HasKey(s => s.StatusRaw);

        // Composite PK (SCHEMA §3): TVmaze returns several titles per country, so country_code alone is
        // not unique per show. aka_order preserves the API array order — the mapper picks the lowest for
        // the target country. Cascade from tvmaze_show so a source row's akas go with it.
        var tvmazeAka = modelBuilder.Entity<TvmazeAka>();
        tvmazeAka.ToTable("tvmaze_akas");
        tvmazeAka.HasKey(a => new { a.TvmazeShowId, a.CountryCode, a.Name });
        tvmazeAka.HasOne<TvmazeShow>().WithMany().HasForeignKey(a => a.TvmazeShowId)
            .OnDelete(DeleteBehavior.Cascade);
        tvmazeAka.HasIndex(a => new { a.TvmazeShowId, a.CountryCode, a.AkaOrder })
            .HasDatabaseName("ix_tvmaze_akas_pick");

        modelBuilder.Entity<ImdbTitle>().ToTable("imdb_title");
        modelBuilder.Entity<ImdbTitle>().HasKey(t => t.Tconst);

        var imdbEpisode = modelBuilder.Entity<ImdbEpisode>();
        imdbEpisode.ToTable("imdb_episode");
        imdbEpisode.HasKey(e => e.Tconst);
        imdbEpisode.HasOne<ImdbTitle>().WithMany().HasForeignKey(e => e.ParentTconst)
            .OnDelete(DeleteBehavior.Cascade);
        imdbEpisode.HasIndex(e => new { e.ParentTconst, e.SeasonNumber, e.EpisodeNumber })
            .HasDatabaseName("ix_imdb_ep_parent");

        modelBuilder.Entity<ImdbAkas>().ToTable("imdb_akas");
        modelBuilder.Entity<ImdbAkas>().HasKey(a => new { a.TitleId, a.Ordering });

        modelBuilder.Entity<ImdbDatasetMeta>().ToTable("imdb_dataset_meta");
        modelBuilder.Entity<ImdbDatasetMeta>().HasKey(m => m.DatasetName);

        // ---- staging tables (SCHEMA.md §5) — the myshows source layer, truncate-and-reload ----
        modelBuilder.Entity<StagingShow>().ToTable("staging_show");
        modelBuilder.Entity<StagingShow>().HasKey(s => s.MyshowsId);
        modelBuilder.Entity<StagingShow>().Property(s => s.NeedsReview).HasColumnName("needs_review");

        var stagingEpisode = modelBuilder.Entity<StagingEpisode>();
        stagingEpisode.ToTable("staging_episode");
        stagingEpisode.HasKey(e => new { e.ShowMyshowsId, e.SeasonNumber, e.EpisodeNumber });
        stagingEpisode.Property(e => e.IsSpecial).HasColumnName("is_special");

        modelBuilder.Entity<StagingImdbId>().ToTable("staging_imdb_id");
        modelBuilder.Entity<StagingImdbId>().HasKey(i => i.MyshowsId);

        // v_staging_issues is a hand-written view (SCHEMA.md §6), created in migration 0003. EF maps it
        // as keyless and ToView() means EF will NOT try to create it — the CREATE VIEW lives in the
        // migration SQL, because views are not EF-first-class.
        modelBuilder.Entity<StagingIssue>().ToView("v_staging_issues").HasNoKey();

        // tracked_show.tvmaze_show_id — the real FK to tvmaze_show (SCHEMA.md §2). A shadow property:
        // the Domain type carries only TvmazeId (the denormalized readable copy), because Domain must
        // not reference a source table (SPEC P3). The mapper keeps the two columns equal. NOT NULL: a
        // mapped row cannot exist without its source row — that is the rebuildability property (P4).
        show.Property<int>("tvmaze_show_id");
        show.HasOne<TvmazeShow>().WithOne()
            .HasForeignKey<TrackedShow>("tvmaze_show_id")
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
        show.HasIndex("tvmaze_show_id").IsUnique().HasDatabaseName("ix_tracked_show_tvmaze_show_id");

        // The one place ValueGenerated is set, applied to every property (SCHEMA.md §1a).
        ApplyNeverValueGeneration(modelBuilder);
    }

    // The SQLite provider defaults an INTEGER PK to ValueGenerated.OnAdd (a store-generated rowid).
    // Ids are client-generated (ADR-026/027), so every property must be Never. Setting it explicitly
    // here — after all entity configuration — is what beats the provider default, and it is applied to
    // every property so no entity can opt out by forgetting. Asserted reflectively in the tests.
    private static void ApplyNeverValueGeneration(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }
    }
}
