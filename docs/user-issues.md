*Issues*
1. Discovery shows 404 for GET newshows and GET upcoming shows
2. Details page for tracked show almost empty - it doesnt show episodes, description, etc
3. Details button in search not working, no way to add to tracking (see improvement 10)
4. During episodes refresh I saw error: EpisodeRefresh: show 83 failed; leaving for the next run. System.InvalidOperationException: Unable to save changes because a circular dependency was detected in the data to be saved: 'Episode [Modified] <-                                   Index { 'show_id', 'season_number', 'episode_number' } Episode [Modified] <-                                                                            Index { 'show_id', 'season_number', 'episode_number' } Episode [Modified]
5. Tvmaze status In Development should be matched to In Development
6. Shows list doesnt display seasons/episodes
7. Interface feel slow - needs investugating further
8. Details for tracked shows should be opened by clicking on title, untrack button should be moved to details, also ther should be possible to change category there - move to started, later or cancelled (since finished is automatic)
9. After fetching titles on details and setting localized title - its not updated on main list, requires to switch category to update view

*Improvements*
1. Categories should be links - not dropdown
2. Started category should have sort option - by name, by show air date
3. Show status in list should be displayed next to show
4. Right now in started I can see shows where I watched all episodes so far, there should be checkbox (disabled by default) to display such shows. They have to be displayed after shows with unwatched episodes under title 'Waiting for new episodes'. Also it should not display unaired episodes, I think it needs separate page - upcoming episodes, which contains started shows containing only episodes with air date in future (or unknown) for each shows, all seasons expanded.
5. Progress log should be added to all jobs, not only import
6. For insignificant_specials - display them only on details page, under separate section at bottom
7. Details page needs sync button, so I can sync data for single show manually
8. Add name filter to lists
9. imdb diff on details page - show only major (like only on imdb) and hide title,runtime ones under View All button (ask which kind to show/hide if unclear)
10. Search page and Search details should
21. Shows without country: where language is filled, display it as country everywhere (tracked list, details, search, discovery + country filter). Example: tvmaze 84377 Dead-End Job (Netflix, country null, language Korean). DONE 2026-10-07 (ADR-036, SPEC R15) — run "Re-derive countries" once to fill existing rows
22. Country and TVmaze rating as grey chips on the title line, after the user rating, with a dot separator after the status/user-rating chips — in all lists. DONE 2026-10-31
23. Details page: truncated episode description in the free space, ⓘ popup with episode image + full description (icon hidden when there is neither), watched-at date on watched rows of tracked shows, per-episode TVmaze rating, per-episode duration. DONE 2026-10-31
24. Started tab: per-episode duration at the right before the air date, season total before the episode count. DONE 2026-10-31
25. New tab "Updates" next to Finished, same layout as Upcoming, four sections: Ended Shows (status became terminal in the past 60 days, order by that date desc, name + date), Renewed Shows (left terminal for an airable status in the past 60 days, order by that date desc, name + current status + date), Updated Lately (updated on TVmaze in the past 30 days, order by that date desc, name + date), Recently Watched (episodes watched in the past 30 days, order by watched-at desc, show name + S#E# title + date). DONE 2026-10-31 (ADR-038, migration 0011)
