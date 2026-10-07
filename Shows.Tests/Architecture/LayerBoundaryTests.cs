using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NetArchTest.Rules;
using Shows.Application;
using Shows.Domain;
using Shows.Infrastructure.Persistence;
using Xunit;

namespace Shows.Tests.Architecture;

/// <summary>
/// Step 8.1 — the layer-boundary invariants from <c>ARCHITECTURE.md §2/§3</c> and the <c>AGENTS.md</c>
/// hard rules, encoded as executable tests. Each rule is the compiler-visible proxy for an invariant
/// that is otherwise only prose: a rule that cannot fail is worthless, so every one is proven to reject
/// an injected violation (recorded in <c>docs/LOG.md</c>).
/// </summary>
/// <remarks>
/// The test project references Domain/Application/Infrastructure only — never the MAUI App or the job
/// Exes (they are <c>net8.0-windows</c>, this is <c>net8.0</c>). Rules that need those assemblies are
/// therefore source-file scans, and rules that need repo files (the Domain csproj, the migrations)
/// locate the repo root by walking up to the folder holding <c>Shows.sln</c>.
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class LayerBoundaryTests
{
    private static readonly Assembly ApplicationAssembly = typeof(ICatalogProvider).Assembly;
    private static readonly Assembly DomainAssembly = typeof(TrackedShow).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(ShowsDbContext).Assembly;

    // ---- Rule 1: Application references no EF / HTTP / MAUI / Newtonsoft ----

    [Fact]
    public void Application_does_not_reference_ef_http_maui_or_newtonsoft()
    {
        // ARCHITECTURE §2: Application defines interfaces; Infrastructure implements them. An EF, HTTP,
        // MAUI or Newtonsoft type reaching Application is the layer inversion the split exists to prevent.
        var result = Types.InAssembly(ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "System.Net.Http",
                "Microsoft.Maui",
                "Newtonsoft")
            .GetResult();

        Assert.True(result.IsSuccessful, Offenders("Application types depending on EF/HTTP/MAUI/Newtonsoft", result));
    }

    // ---- Rule 3: Domain has zero packages and is plain C# ----

    [Fact]
    public void Domain_csproj_declares_no_package_references()
    {
        // ARCHITECTURE §3 / AGENTS.md: Shows.Domain has zero NuGet dependencies. The csproj is the
        // ground truth — a PackageReference here is the violation, regardless of what the code uses.
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "Shows.Domain", "Shows.Domain.csproj"));

        Assert.DoesNotContain("<PackageReference", csproj, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Domain_types_are_plain_csharp_with_no_provider_or_serialization_dependencies()
    {
        // The "no EF attributes, no JSON attributes, plain C#" half of ARCHITECTURE §3. Domain may use
        // the base runtime (System.*), but an EF/JSON/HTTP/DataAnnotations dependency means a provider
        // concern leaked into the entity model.
        var result = Types.InAssembly(DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "System.Text.Json",
                "Newtonsoft",
                "System.Net.Http",
                "System.ComponentModel.DataAnnotations")
            .GetResult();

        Assert.True(result.IsSuccessful, Offenders("Domain types depending on EF/JSON/HTTP/DataAnnotations", result));
    }

    // ---- Rule 4: UI references Infrastructure only in the composition root ----

    [Fact]
    public void Only_the_composition_root_in_the_app_references_infrastructure()
    {
        // ARCHITECTURE §2: the U->I edge is composition-root only. The Avalonia head is a plain net8.0
        // desktop app, but its assembly is still not referenced by this test project, so the rule is a
        // source scan: of every hand-written .cs under Shows.App.Avalonia, only the composition roots
        // (Program.cs builds the container; App.axaml.cs is the other sanctioned entry point) may name
        // Shows.Infrastructure. No View or ViewModel may.
        var appDir = Path.Combine(RepoRoot(), "Shows.App.Avalonia");
        var compositionRoots = new[] { "Program.cs", "App.axaml.cs" };
        var offenders = Directory
            .EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsGenerated(f))
            .Where(f => !compositionRoots.Contains(Path.GetFileName(f), StringComparer.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("Shows.Infrastructure", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoRoot(), f))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Only Program.cs / App.axaml.cs may reference Infrastructure in the UI: " + string.Join(", ", offenders));
    }

    // ---- Rule 5: source-table containment ----

    [Fact]
    public void Only_the_mapper_and_its_seams_reference_the_source_tables()
    {
        // SPEC P3/R11: the tvmaze_*/imdb_* source tables are written by the mapper and read by nothing
        // but the mapper and the provider that feeds it. A repository, use case or job reaching into a
        // source entity is the second data path R11 exists to prevent. Enforced on the entity-type
        // namespace (compiler-visible), not SQL strings. The allow-list is every legitimate consumer;
        // the step 3.2 repository sets the tracked_show->tvmaze_show FK by property-name STRING, so it
        // must NOT appear here — that is the assertion that keeps the shell in the mapper.
        var allowed = new[]
        {
            nameof(TvmazeShowMapper),      // the sole writer of source rows
            nameof(ShowsDbContext),        // declares the source DbSets
            "TvmazeJsonMapper",            // parses TVmaze JSON into source entities (provider seam)
            "ITvmazeShowSource",           // the fetch seam, returns TvmazePayload
            "TvmazeCatalogProvider",       // implements the seam, builds TvmazePayload
            "TvmazePayload",               // the payload DTO itself
            "Promoter",                    // --promote fetches a TvmazePayload and hands it to the mapper
            "ImdbImporter",                // step 7.4: the sole writer of the imdb_* source tables + meta
        };

        var result = Types.InAssembly(InfrastructureAssembly)
            .That()
            .DoNotResideInNamespaceStartingWith("Shows.Infrastructure.Persistence.Sources")
            .And()
            .DoNotHaveNameMatching(string.Join("|", allowed))
            .ShouldNot()
            .HaveDependencyOn("Shows.Infrastructure.Persistence.Sources")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            Offenders("Infrastructure types outside the allow-list depending on the source tables", result));
    }

    // ---- Rule 6: user-data containment ----

    [Fact]
    public void The_mapper_does_not_reference_the_user_owned_tables_it_must_never_write()
    {
        // SPEC P4/ADR-023: the mapper never writes user_show or ui_season_state. It DOES read
        // episode_watch to recompute progress (SPEC P4 allows the read), so episode_watch is not banned
        // here — its write-containment is the behavioral rebuild drill in step 1.5. UserShow and
        // UiSeasonState have no legitimate mapper use, so any dependency is a violation.
        var result = Types.InAssembly(InfrastructureAssembly)
            .That()
            .HaveNameMatching(nameof(TvmazeShowMapper))
            .ShouldNot()
            .HaveDependencyOnAny("Shows.Domain.UserShow", "Shows.Domain.UiSeasonState")
            .GetResult();

        Assert.True(result.IsSuccessful, Offenders("the mapper depending on user_show or ui_season_state", result));
    }

    // ---- Rule 7: no store-generated keys ----

    [Fact]
    public void No_entity_property_is_store_generated()
    {
        // ADR-026/027: ids are minted in-process, never by the store. The SQLite provider defaults an
        // INTEGER PRIMARY KEY to ValueGenerated.OnAdd, so the MODEL — not the migration — is where this
        // silently regresses. ShowsDbContext.ApplyNeverValueGeneration sweeps every property to Never;
        // this asserts the sweep actually covers the whole model.
        var options = new DbContextOptionsBuilder<ShowsDbContext>()
            .UseSqlite("Data Source=:memory:")
            .UseSnakeCaseNamingConvention()
            .Options;

        using var context = new ShowsDbContext(options);

        var offenders = context.Model.GetEntityTypes()
            .SelectMany(t => t.GetProperties())
            .Where(p => p.ValueGenerated is Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd
                or Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate)
            .Select(p => $"{p.DeclaringType.ShortName()}.{p.Name} = {p.ValueGenerated}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "store-generated keys (ADR-026/027): " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_migration_emits_autoincrement()
    {
        // ADR-026: no AUTOINCREMENT anywhere in the shipped DDL. The model assertion above is the
        // primary guard; this catches a hand-edited migration that reintroduces it.
        var migrationDir = Path.Combine(
            RepoRoot(), "Shows.Infrastructure", "Persistence", "Migrations");

        var offenders = Directory
            .EnumerateFiles(migrationDir, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(f => File.ReadAllText(f).Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "migrations containing AUTOINCREMENT (ADR-026): " + string.Join(", ", offenders));
    }

    // ---- Rule 8: lease discipline (type-level half) ----

    [Fact]
    public void Every_bulk_writer_resolves_the_writer_lease()
    {
        // ADR-028: a bulk writer must coordinate through IWriterLease, never hand-roll lease SQL. The
        // behavioural half (a renewal that updates zero rows stops the loop) lives in step 1.6; this is
        // the cheap type-level guard that a new bulk writer cannot forget the lease entirely. Full type
        // names, since the writers span Import (scraper/resolver/promoter) and Jobs (the refresh jobs).
        var bulkWriters = new[]
        {
            "Shows.Infrastructure.Import.ImdbIdScraper",
            "Shows.Infrastructure.Import.TvmazeIdResolver",
            "Shows.Infrastructure.Import.Promoter",
            "Shows.Infrastructure.Import.ImdbImporter",
            "Shows.Infrastructure.Jobs.ShowRefreshJob",
            "Shows.Infrastructure.Jobs.RefreshUpdatedShowJob",
            "Shows.Infrastructure.Jobs.AkasJob",
            "Shows.Infrastructure.Jobs.ImdbEnrichmentJob",
            "Shows.Infrastructure.Jobs.CountryBackfillJob",
        };

        var offenders = bulkWriters
            .Select(name => InfrastructureAssembly.GetType(name, throwOnError: true)!)
            .Where(type => !type.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType.Name == nameof(IWriterLease))))
            .Select(t => t.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "bulk writers without an IWriterLease dependency (ADR-028): " + string.Join(", ", offenders));
    }

    // ---- helpers ----

    // FailingTypeNames is already a collection of display strings, so no Mono.Cecil type is named here.
    private static string Offenders(string what, TestResult result) =>
        $"{what} — offenders: {(result.FailingTypeNames is null ? "(none)" : string.Join(", ", result.FailingTypeNames))}";

    // Walk up from the test assembly's output directory to the folder containing Shows.sln. Robust to
    // the bin/Debug/net8.0 depth without hard-coding "..\..\..".
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Shows.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate the repo root (Shows.sln).");
    }

    // Generated MAUI/WinUI files (*.g.cs, *.g.i.cs, the obj/ and bin/ trees) are not hand-written and
    // may name framework types; they are not ViewModels or Views.
    private static bool IsGenerated(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);
}
