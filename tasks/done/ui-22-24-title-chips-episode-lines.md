---
status: current
---

# Step UI-22 — Title-line chips, and episode rows that fill the details page

## Read
- `docs/STATE.md` — current state; the UI head is Avalonia (ADR-033), improvement 21 was the previous UI step.
- `docs/SPEC.md` P5 — "nothing derivable is stored": the season runtime total is a computed aggregate, never a column.
- `docs/SPEC.md` R12 / ADR-034 — the episode-type vocabulary; `InlineEpisodeRules.IsListable` is the one predicate for "displayable".
- `docs/ARCHITECTURE.md` §2 — the layer graph: `Shows.Application` may not name `HtmlStripper` (Infrastructure), so the provider mapper strips `summary` into the display read model.
- `docs/SCHEMA.md` §1a — id encoding. Not touched here: no schema change, no new column.

## Do
1. Read model: `IEpisodeRepository.SumRuntimeBySeasonAsync` / `…ForShowsAsync` (grouped `SUM(runtime_minutes)`
   over the SAME displayable predicate as `CountDisplayableEpisodesBySeason*`); `ShowDetails` gains
   `WatchedAtByEpisodeId` (built from watch rows already loaded — no extra query); `CardSeasons` gains
   `RuntimeMinutesBySeason`.
2. `CatalogEpisode` / `EpisodeCatalogInfo` widened with optional trailing members (`Description`, `ImageUrl`,
   `RatingAverage`, `RuntimeMinutes`) so the details pages have something to fill their space with.
3. UI: `RuntimeFormat` (presentation-only formatting), chip line on all three card/detail headers
   (status → user rating → dot → country → TVmaze rating), two-line episode rows with the info flyout.

## Acceptance criteria
User request, verbatim, in order:
- [ ] Country and TVmaze rating move onto the title line, after the user rating, as grey chips, with a dot
      separator after the status/user-rating chips. In **all** lists.
- [ ] Details page: truncated episode description in the free space; an info icon opening a popup with the
      episode image and full description; for tracked shows, "watched at" date on watched episodes.
- [ ] The info icon is absent when there is neither a poster nor a description (both details and
      search/discovery). The watched date is tracked-only.
- [ ] Details row geometry: `S8E22` marker top-aligned; air date next to the title (1); watched date next to
      it (2); short description as the second line (3); info button at the right edge, vertically centred.
- [ ] Per-episode TVmaze rating next to the title; duration under the marker in small grey type — still two lines.
- [ ] Started tab: per-episode duration, and a total on the season line.
- [ ] Correction (2026-10-31): on the Started tab the duration sits at the **right, before the air date**, and
      the season total sits **before the episode count**.

## Constraints
- `TreatWarningsAsErrors` + `analysis-level latest-all`: an unused field or a directly-touched
  `[ObservableProperty]` backing field (`MVVMTK0034`) fails the build.
- A horizontal `StackPanel` measures a `TextBlock` at its full single-line width, so chips after a long title
  are pushed off-screen. Use `Grid ColumnDefinitions="*,Auto"` (star first, Auto second) — see the LOG entry.
- `<Flyout>` written as a `<Button>` **child** binds to `Button.Content` (the `[Content]` property), not
  `Button.Flyout`: the glyph disappears and the popup never opens. Use `<Button.Flyout>`.
- The row's `PointerPressed` is the watch toggle, so the info button's press bubbles into it. Ignore presses
  whose `e.Source` has a `Button` ancestor — do **not** mark the event handled, which suppresses the flyout.
- No `ConfigureAwait(false)` where a bound property is written afterwards (Avalonia thread check).
- The runtime total must be summed over exactly the episodes the header's count describes, or the two numbers
  disagree.

## Evidence
Appended to `docs/LOG.md` as "2026-10-31 — UI improvements 22–24". `docs/STATE.md` updated;
`docs/user-issues.md` items 22–24 marked done.
