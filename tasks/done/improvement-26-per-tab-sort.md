---
status: current
---

# Step UI-26 — Per-tab persisted sort, tighter season rows, chips that follow the title

## Read
- `docs/STATE.md` — current state; the UI head is Avalonia (ADR-033), improvements 22–25 were the previous UI steps.
- `docs/SPEC.md` P4 — user data lives in the DB; a sort choice is a **UI preference**, so it belongs in
  `Services/Preferences.cs`, not in a table.
- `docs/SPEC.md` P5 — "nothing derivable is stored". Not touched here: no schema change, no new column.
- `docs/LOG.md` "2026-10-31 — UI improvements 22, 23, 24" — the chip line this step re-shapes.

## Do
1. `Services/Preferences.cs`: make `StorePath` a **property**. `Default` was declared above the `StorePath`
   field, and static initializers run in declaration order, so `Load()` ran with a null path — every saved
   preference silently read its default on every launch.
2. `TrackingViewModel`: `SortPreferenceKey(ShowCategory)` (explicit `switch`, not interpolation — CA1308),
   `ReadSortOption`/`RestoreSortOption`, restore in `OnSelectedCategoryChanged` **before** `LoadAsync()`,
   and suppress the persist-and-resort hook with `_restoringSortOption` while a tab switch restores.
3. UI: `WrapPanel ItemSpacing="6" LineSpacing="2"` chip run (title → dot → chips) in `TrackedShowCardView`,
   `ShowCardView`, `ShowDetailsView`, `UpdatesRowView`; `ShowTitleChipSeparator` on the three card/detail view
   models; Started-card padding/spacing reduced.

## Acceptance criteria
User request, verbatim, in order:
- [x] "Make sorting selection saved across app restarts on started and later tabs" — each tab restores **its
      own** saved order, and a tab switch does not write the incoming tab's value under the outgoing key.
- [x] "Reduce spacing between seasons and episodes in starting tab".
- [x] "Move status, language and rating chips from right next to title, separated by dot (all lists)",
      clarified verbatim: "they should be same line as the title, but without right alignment as it is now,
      it should be Title<dot separator>Ships" — chips flow immediately after the title text, wrapping when
      they do not fit, in every list and on the details page.
- [x] No leading dot when a card renders no chip (and, in `UpdatesRowView`, no dot that could dangle at the
      end of a row when a show has no status).

## Constraints
- `TreatWarningsAsErrors` + `analysis-level latest-all`: CA1308 rejects `ToLowerInvariant`, and
  `MVVMTK0034` rejects reading a `[ObservableProperty]` backing field directly — use the generated property.
- **Supersedes** the improvement 22–24 rule "chips after a long title need `Grid ColumnDefinitions="*,Auto"`".
  That rule kept the chips *visible*; a `WrapPanel` keeps them visible **and** flows them after the title,
  because `WrapPanel.MeasureOverride` hands each child the constraint width (a `TextWrapping="Wrap"` TextBlock
  returns its longest line) while a horizontal `StackPanel` measures it at infinite width.
- Invisible children cost nothing in either panel (`Layoutable.MeasureCore` returns `Size()` when
  `IsVisible` is false; `WrapPanel` skips `ItemSpacing` for them) — so a separator can be a plain hidden
  element rather than a layout change.
- `InlineUIContainer` is **not** the answer: `Inline` is a `TextElement`, not a `Control`, so `DataContext`
  inheritance is unreliable, and it defaults to `BaselineAlignment.Top`.
- No `ConfigureAwait(false)` where a bound property is written afterwards (Avalonia thread check).
- **This session cannot click.** `SetCursorPos` is a no-op, `GetForegroundWindow()` returns 0, `SendInput`
  clicks are dropped, and posted `WM_LBUTTONDOWN`/`UP` never enter Avalonia's input pipeline. Verify by
  seeding state through a file or a temporary constructor call, then capturing with `PrintWindow`.
- Rebuild after `git stash pop` — the on-disk exe is still the stashed build, and a capture taken before
  rebuilding shows the old layout. Full-window PNG hashes are not comparable between runs (posters load
  asynchronously).

## Evidence
Appended to `docs/LOG.md` as "2026-10-31 — improvement 26". `docs/STATE.md` updated (the chip-shape paragraph
rewritten, the improvements list extended); `docs/user-issues.md` item 26 marked done.
Commands and observed output in the LOG entry: `dotnet build Shows.sln -v q` → `0 Warning(s) / 0 Error(s)`;
`dotnet test Shows.Tests -v q` → `Failed: 0, Passed: 386`; the seeded-store probe showing
`Started stored=3 resolved=Last added`, `Later stored=1 resolved=Air date`,
`Cancelled stored=2 resolved=Last aired`, `Finished stored=0 resolved=Name`.
