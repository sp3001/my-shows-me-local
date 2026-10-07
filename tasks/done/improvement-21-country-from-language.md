---
status: done
---

# Step improvement-21 — Display country falls back to language

## Read
- `docs/STATE.md` — current state at step start (320 tests, migrations through 0009)
- `docs/SCHEMA.md §2/§3` — `tracked_show` and `tvmaze_show` column blocks the new columns extend
- `docs/SPEC.md §2 field table + R11/R12` — mapped-vs-verbatim split the fallback must respect
- `docs/DECISIONS.md ADR-022/ADR-023` — mapper is the sole writer of mapped rows; source is verbatim

## Do
- `Shows.Domain/CountryRules.cs` — the one rule: `country_name ?? country_code ?? language`
  (Domain, because every layer must agree; mirrors `ShowStatusRules`).
- `TrackedShow.Country` + `tvmaze_show.country_name`/`country_code` (verbatim) + migration
  `0010_ShowCountry` (schema only — **no data backfill SQL**, the mapper is the only writer).
- `TvmazeJsonMapper`: `ReadBroadcasterCountry` (network else webChannel, nested `country`),
  `MapShow` stores verbatim, `MapCatalogShow` applies the rule → search/discovery cards.
- `TvmazeShowMapper.ApplyShowFields` fills `tracked_show.country` through the rule;
  `RemapCountryFromStoredPayloadAsync` re-derives existing rows from `tvmaze_show.raw_json`
  (no network; unparseable/missing payload = skip and keep, logged).
- `Enrichment --country` (`CountryBackfillJob`, lease `enrichment_country`, job_run
  `Enrichment.Country`) + `JobKind.EnrichCountry` + Manage-page button "Re-derive countries".
- `ScheduleParser` builds the country facet from `MapCatalogShow`'s value, so facet and card agree.
- UI: tracked card meta line and details page show the country; card signature includes it.

## Acceptance criteria
(forked from the user request; no plan step exists for this improvement)
- [x] TVmaze 84377 shape (webChannel Netflix, `country: null`, `language: "Korean"`) yields
      "Korean" on tracked cards, tracked details, search cards, discovery cards, and the discovery
      country facet (facet option count and card value are the same string).
- [x] A real broadcaster country always wins over the language (no English show collapses to "English").
- [x] A show with neither stays NULL and still counts as "Not set" in the facet.
- [x] Source columns stay verbatim (R11): `tvmaze_show.country_name`/`country_code` are null for 84377;
      `language` keeps its own column.
- [x] Existing rows backfill without network: `Enrichment --country` re-parses `raw_json`; a re-run
      reports 0 changed; soft-deleted shows are never targets (R8).
- [x] Migration is schema-only; no UPDATE of mapped rows outside the mapper.
- [x] `dotnet build Shows.sln` 0 warnings; full test suite green.

## Constraints
- The mapper is the SOLE writer of `tracked_show` (SPEC P3/R11) — the backfill goes through it, not SQL.
- `--country` mode must not touch the network (raw_json is the payload of record; SCHEMA §3).
- Lease renewal failure stops writing (ADR-028); job joins the bulk-writer architecture rule.
- The UI must not re-derive the fallback anywhere — it reads `TrackedShow.Country` /
  `CatalogShow.CountryName` as stored.

## Evidence
In the 2026-10-07 `improvement-21` entry in `docs/LOG.md`.
