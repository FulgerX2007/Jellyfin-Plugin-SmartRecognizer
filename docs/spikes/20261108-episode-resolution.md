# Spike: how Jellyfin 12.4 treats season/episode numbers from providers

Source checked: `jellyfin/jellyfin` commit `83d0d0e` ("Bump version to 12.4"), the same commit the
`Jellyfin.Controller 12.4.0` NuGet package was built from. No code was written in this spike, so
there are no tests for it.

## Answers

| # | Question | Answer | Where |
|---|---|---|---|
| 1 | Is `IndexNumber` from a provider used when the built-in parser found none (`3 серия.mkv`)? | **Yes.** The base merge copies `IndexNumber` when the target value is `null`. | `MetadataService.MergeData` (`if (replaceData \|\| !target.IndexNumber.HasValue)`) |
| 2 | Can a provider fix a **wrong** non-null number? | `IndexNumber`: **only in "Replace all metadata" mode**. `ParentIndexNumber`: **yes, always** — an explicit season from a provider wins over the path-parsed one. | `MetadataService.MergeData`; `EpisodeMetadataService.MergeData` (`mergeMetadataSettings && source.ParentIndexNumber.HasValue`) |
| 3 | Absolute anime number → season: does it apply in the same scan? | **Partly.** Seasons are created in `SeriesMetadataService.AfterMetadataRefresh` → `CreateSeasonsAsync`, which runs before episodes are refreshed (it reads season numbers from paths). An episode moved to a season that does not exist yet gets its season on the **next** series refresh. | `SeriesMetadataService.CreateSeasonsAsync`, `UpdateSeriesChildrenInfoAsync` |
| 4 | Does Jellyfin parse `Сезон 2` and `2 сезон` folders? | **Yes, both.** The keyword list includes `сезон`; the "number first" form is handled by `ProcessPre`. | `Emby.Naming/TV/SeasonPathParser.cs`, `SeasonPathParserTests` (`/Drive/Сезон 1`) |
| 5 | Do Season/Episode lookups see `SeriesProviderIds` set by our Series provider in the same scan? | **Yes.** The series is refreshed before its children, and `GetLookupInfo()` copies `series.ProviderIds`. | `Folder.RefreshAllMetadataForContainer`, `Episode.GetLookupInfo`, `Season.GetLookupInfo` |
| 6 | `IExternalId` / `IExternalUrlProvider` signatures | `IExternalId { string ProviderName; string Key; ExternalIdMediaType? Type; bool Supports(IHasProviderIds) }`; links live in `IExternalUrlProvider { string Name; IEnumerable<string> GetExternalUrls(BaseItem) }`. | `MediaBrowser.Controller/Providers/` |

## Decision

**No custom `IItemResolver` / `ILibraryPostScanTask` for v1.** Providers are enough:

- Episodes the built-in parser could not number get their number from our parser (via the provider).
- Seasons: we always return `ParentIndexNumber` when we know it — Jellyfin prefers it.
- Wrong non-null episode numbers are rare (the built-in parser fails with `null`, not with a wrong
  number). The fix is "Refresh metadata → Replace all metadata". Document this in the README.
- Anime absolute → season: correct after the second series refresh. Document this in the README;
  revisit with a post-scan task only if it turns out to bother users.
- Our own season-folder parsing stays (the parser needs it to number episodes), but Jellyfin
  already creates the `Season` items for `Сезон 2` / `2 сезон`.

## Correction after the live smoke test (Task 23)

The real Jellyfin 12.4 scan showed that answer 2 was wrong in both directions. Before calling
remote providers, `MetadataService` seeds the temporary result with the item's current
`ParentIndexNumber` (`temp.Item.ParentIndexNumber = item.ParentIndexNumber`) and merges each
provider into it with `mergeMetadataSettings: false`. So:

- a **season** number from a remote provider is applied only on "Replace all metadata";
- an **episode** number from a remote provider *is* applied on a normal scan (the temp → item
  merge replaces data in "Scan for new and updated files" mode).

Result before the fix: `Сезон 2/Кухня - Серия 3.mkv`, read by Jellyfin as S7E5, became S7E3.

**Fix:** `EpisodeNumberProvider` (`ICustomMetadataProvider<Episode>` + `IPreRefreshProvider`)
corrects the item's numbers from explicit markers before the temp result is seeded, so every
provider, built-in ones included, looks up the right episode. Verified live: the file became S2E3
and the phantom "Season 7" disappeared. Toggle: `FixEpisodeNumbers` (on by default).
