# Smart Recognizer — Jellyfin plugin

C# / .NET 10 plugin for Jellyfin 12.4 (`targetAbi 12.4.0.0`). Recognizes movies/series from messy file names and merges metadata from TMDb, Kinopoisk (kinopoisk.dev), OMDb and TVDB, with an optional LLM fallback.

## Commands

- Test: `dotnet test`
- Release build (must have 0 warnings — `TreatWarningsAsErrors`): `dotnet build -c Release`
- Package: `jprm plugin build . --output=artifacts` (zip must contain only `Jellyfin.Plugin.SmartRecognizer.dll` + `meta.json`)

## Layout (`src/Jellyfin.Plugin.SmartRecognizer/`)

- `Parsing/` — `ReleaseNameParser`: pure, deterministic `Parse(path, maxYear)`; one class with private methods + `TokenDictionaries` (no rule classes).
- `Matching/` — `TitleNormalizer`, `MatchScorer` (0.6 title + 0.3 year + 0.1 kind), `CandidateResolver` (staged search TMDb → Kinopoisk → TVDB → OMDb, stops at the first confident match), `RecognitionPipeline` (main flow), `IReleaseFallback`.
- `Sources/` — `IMetadataSource` / `IEpisodeSource`, `SourceHttpClient` (cache, rate limit, retries, daily quota), one folder per source.
- `Aggregation/` — `FieldPriority` (fixed, language-dependent) and static `MetadataMerger`.
- `Providers/` — Jellyfin providers; `EpisodeNumberProvider` is a pre-refresh provider that fixes season/episode numbers.
- `Ai/` — raw-HTTP LLM clients (Anthropic Messages API with structured outputs; OpenAI-compatible with `json_schema`), `AiReleaseRecognizer`, `AiStateStore` (JSON file).

## Rules

- Every new file-name case goes into the corpus `tests/Jellyfin.Plugin.SmartRecognizer.Tests/TestData/release-names.json` (names that can exist on disk — no `/` inside a name).
- Do **not** register providers (`IRemoteMetadataProvider`, `IRemoteImageProvider`, `IExternalId`, `IExternalUrlProvider`, `ICustomMetadataProvider`) in DI — Jellyfin discovers them from the assembly. Register only their dependencies in `PluginServiceRegistrator`, and add every provider to `DependencyInjectionTests`.
- Read configuration through `IPluginConfigurationAccessor` on every call (settings change at runtime); `PluginConfiguration` must stay `XmlSerializer`-friendly (no dictionaries).
- Host assemblies are referenced with `ExcludeAssets=runtime` / `FrameworkReference Microsoft.AspNetCore.App`; add no NuGet runtime dependencies (they would be shipped and can clash with the server).
- Sources never throw for HTTP/JSON problems (log + empty result) and never log URLs or keys. Tests use `FakeHttpMessageHandler` with fixtures in `TestData/<source>/` — no real network calls.
- Conservative matching: below `MinConfidence` return "no match", never guess. AI answers are hints only — IDs from the model are ignored.
- For Claude: no `temperature`/`top_p`/`top_k` and no forced `tool_choice` (rejected by current models); use `output_config.format`.
- Jellyfin 12.4 behaviour around season/episode numbers is documented in `docs/spikes/20261108-episode-resolution.md` (read it before touching episode code).
