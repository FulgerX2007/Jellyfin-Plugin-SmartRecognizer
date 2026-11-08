# Jellyfin Smart Recognizer

## Overview
- Плагин Jellyfin `Jellyfin.Plugin.SmartRecognizer` (C#, .NET): распознаёт фильмы и сериалы по имени файла/папки и подтягивает метаданные из TMDb, Кинопоиска, OMDb (IMDb) и TVDB.
- Проблема: встроенный парсер `Emby.Naming` плохо справляется с русскими релизами (`Тёмный рыцарь (The Dark Knight) 2008 BDRip [Дубляж]`), scene/торрент-именами, аниме (`[Group] Title - 05 [1080p]`) и нестандартной структурой сезонов (`Сезон 2/03 серия.mkv`, `1x05`). В результате — неверные совпадения или «Unknown».
- ИИ (LLM) — опциональный запасной вариант: вызывается только когда правила парсера не справились или ни один кандидат не набрал порог уверенности. Выключен по умолчанию; плагин полностью работоспособен без него.
- Польза: корректное распознавание без ручного переименования и «Identify», русскоязычные описания из Кинопоиска, рейтинги IMDb/RT, точные данные по сериям из TVDB.
- Интеграция: плагин добавляет собственные metadata/image-провайдеры. Пользователь включает их в настройках библиотеки и ставит выше встроенных. Провайдер проставляет `ProviderIds` (Tmdb/Imdb/Tvdb/Kinopoisk), чтобы встроенные провайдеры тоже работали по точным ID.

## Context (from discovery)
- рабочая директория пустая — проект создаётся с нуля (git-репозиторий тоже нужно инициализировать)
- решение архитектора: **всё на C# в одном плагине** (без Go-сервиса), т.к. Jellyfin загружает только .NET-сборки
- точки расширения Jellyfin:
  - `BasePlugin<PluginConfiguration>`, `IHasWebPages` — плагин и страница настроек
  - `IPluginServiceRegistrator` — регистрация **зависимостей** провайдеров в DI (источники, resolver, merger, pipeline, `HttpClient`)
  - `IRemoteMetadataProvider<Movie, MovieInfo>`, `<Series, SeriesInfo>`, `<Season, SeasonInfo>`, `<Episode, EpisodeInfo>`, `IRemoteImageProvider`, `IExternalId`, `IExternalUrlProvider` (10.10+) — **Jellyfin находит их сам**, сканируя сборку плагина; в DI их НЕ регистрируем (иначе возможны дубликаты), их конструкторы получают зависимости из DI
  - `IHasOrder` — порядок провайдера по умолчанию
- зависимости: NuGet `Jellyfin.Controller`, `Jellyfin.Model` (версия = целевая версия сервера). `IMemoryCache` уже зарегистрирован сервером. Все сборки, которые предоставляет хост, подключаются с `<ExcludeAssets>runtime</ExcludeAssets>` и не попадают в zip плагина. Тесты — xUnit, NSubstitute
- ⚠️ пересечение со встроенными провайдерами: TMDb и OMDb уже есть в Jellyfin, TVDB — официальный плагин. Наш плагин их не заменяет, а добавляет собственный парсинг + агрегацию + Кинопоиск. Jellyfin запускает все включённые провайдеры и берёт первое непустое значение поля, поэтому встроенные TMDb/OMDb рекомендуется отключить или опустить ниже (иначе при «нет совпадения» у нас встроенный TMDb подставит наивное совпадение)

## Development Approach
- **testing approach**: TDD — сначала тесты (для парсера — корпус реальных имён → ожидаемый результат), затем код
- complete each task fully before moving to the next
- make small, focused changes
- **CRITICAL: every task MUST include new/updated tests** for code changes in that task
  - tests are not optional - they are a required part of the checklist
  - write unit tests for new functions/methods
  - write unit tests for modified functions/methods
  - add new test cases for new code paths
  - update existing test cases if behavior changes
  - tests cover both success and error scenarios
- **CRITICAL: all tests must pass before starting next task** - no exceptions
- **CRITICAL: update this plan file when scope changes during implementation**
- run tests after each change: `dotnet test`
- maintain backward compatibility (конфигурация плагина: новые поля только с дефолтами)

## Testing Strategy
- **unit tests**: обязательны в каждой задаче
  - парсер: data-driven тесты (`[Theory]` + `[MemberData]`) по корпусу `tests/.../TestData/release-names.json` — каждая запись: путь (`/` и `\` разделители) → ожидаемый `ParsedRelease`. Корпус содержит только имена, которые реально встречаются на диске. Растёт с каждой задачей парсера
  - HTTP-клиенты источников и LLM: фейковый `HttpMessageHandler` + записанные JSON-фикстуры (`TestData/<source>/*.json`); проверяется и формат запроса, и разбор ответа; никаких реальных сетевых вызовов
  - провайдеры и pipeline: моки (NSubstitute); конфигурация подаётся через `IPluginConfigurationAccessor` с фиксированным объектом
  - DI wiring test: `ServiceCollection` + `PluginServiceRegistrator` → каждый провайдер создаётся через `ActivatorUtilities.CreateInstance` (ловит забытые регистрации без живого сервера)
- **e2e tests**: UI-тестов в проекте нет. Ранний ручной smoke-тест в Docker Jellyfin — в Task 16; полная ручная проверка — в Post-Completion

## Progress Tracking
- mark completed items with `[x]` immediately when done
- add newly discovered tasks with ➕ prefix
- document issues/blockers with ⚠️ prefix
- update plan if implementation deviates from original scope
- keep plan in sync with actual work done

## Solution Overview

Поток данных при сканировании/«Identify» (`RecognitionPipeline`):

```
Jellyfin scan ──► Provider (Movie/Series/Season/Episode)
                    ▼
               RecognitionPipeline
                    │ 1. есть ProviderIds? → сразу шаг 4
                    │ 2. ReleaseNameParser.Parse(path) → ParsedRelease
                    │ 3. CandidateResolver — поэтапный поиск:
                    │      TMDb → Кинопоиск (если кириллица или язык ru)
                    │      → TVDB (только сериалы) → OMDb (последний фоллбэк)
                    │      стоп, как только кандидат ≥ MinConfidence
                    │    нет совпадения? → [опц.] AiReleaseRecognizer → повтор шага 3
                    │ 4. догрузка деталей по ID из всех включённых источников (параллельно),
                    │    обогащение ID: kp → imdb/tmdb, tmdb → imdb/tvdb; OMDb — по imdb ID
                    │ 5. MetadataMerger (фиксированные приоритеты по полям)
                    ▼
               MetadataResult<T> + ProviderIds
```

Ключевые решения:
- **Один агрегирующий провайдер на тип элемента**, а не провайдер на каждый источник. Jellyfin сливает провайдеры по принципу «первое непустое поле» и не умеет сопоставлять кандидатов между источниками. Нам нужна своя логика: найти фильм в Кинопоиске по русскому названию → взять imdb/tmdb ID → обогатить точными данными. Источники — внутренние клиенты.
- **Поиск поэтапный, обогащение — по ID**: поиск во всех источниках сразу сжёг бы квоты (Кинопоиск — суточная, OMDb — 1000/день) на большой библиотеке. Поиск идёт до первого уверенного совпадения, остальное догружается точечно по ID.
- **Парсер — чистая детерминированная функция** (`path, maxYear → ParsedRelease`): один класс с приватными методами + словари токенов, без отдельных классов правил.
- **Консервативное сопоставление**: лучше не найти ничего (пользователь сделает «Identify»), чем подставить неверный фильм. Порог настраивается.
- **ИИ — только подсказчик, а не источник истины**: LLM возвращает разобранные поля (названия, год, тип, сезон/серия) в строгом JSON; совпадение подтверждается поиском в источниках с тем же порогом. ID от ИИ игнорируются. Вызов только в фоллбэке. Подключается к pipeline через nullable-хук, поэтому реализуется последним. Два клиента: Anthropic (Claude) и OpenAI-совместимый (OpenAI, Ollama, LM Studio — локальный режим без отправки имён файлов наружу).
- **Конфигурация читается на каждом вызове** через `IPluginConfigurationAccessor` — ключи и флажки меняются на лету со страницы настроек; тесты подставляют фиксированный объект.
- **Без лишних зависимостей**: только то, что есть в .NET/Jellyfin (без Polly, без сторонних JSON/HTTP-библиотек; `System.Text.Json`). Сборки хоста не копируются в плагин.

## Technical Details

### Структура репозитория
```
Jellyfin.Plugin.SmartRecognizer.sln
Directory.Build.props, build.yaml           # build.yaml — манифест для jprm
src/Jellyfin.Plugin.SmartRecognizer/
  Plugin.cs, PluginServiceRegistrator.cs
  Configuration/PluginConfiguration.cs, IPluginConfigurationAccessor.cs, configPage.html
  Parsing/      ParsedRelease.cs, ReleaseNameParser.cs, TokenDictionaries.cs
  Matching/     TitleNormalizer.cs, MatchScorer.cs, CandidateResolver.cs, RecognitionPipeline.cs
  Sources/      IMetadataSource.cs, IEpisodeSource.cs, SourceModels.cs, SourceHttpClient.cs,
                Tmdb/, Kinopoisk/, Omdb/, Tvdb/
  Aggregation/  FieldPriority.cs, MetadataMerger.cs
  Providers/    MovieProvider.cs, SeriesProvider.cs, SeasonProvider.cs, EpisodeProvider.cs,
                SmartImageProvider.cs, KinopoiskExternalId.cs, KinopoiskExternalUrlProvider.cs
  Ai/           ILlmClient.cs, AnthropicLlmClient.cs, OpenAiCompatibleLlmClient.cs,
                AiReleaseRecognizer.cs, AiStateStore.cs
tests/Jellyfin.Plugin.SmartRecognizer.Tests/
  TestData/release-names.json, TestData/{tmdb,kinopoisk,omdb,tvdb,ai}/*.json
```

### Модель парсинга
```csharp
public enum MediaKind { Unknown, Movie, Series, Episode }

public sealed record ParsedRelease
{
    public IReadOnlyList<string> Titles { get; init; }    // кандидаты: [RU, EN, ...], очищенные
    public int? Year { get; init; }
    public MediaKind Kind { get; init; }
    public int? Season { get; init; }                     // 0 = specials
    public IReadOnlyList<int> Episodes { get; init; }     // поддержка S01E01-E02
    public int? AbsoluteEpisode { get; init; }            // аниме
    public string? ReleaseGroup { get; init; }
    public string? Resolution { get; init; }              // 1080p, 2160p
    public string? Source { get; init; }                  // BDRip, WEB-DL, ...
    public IReadOnlyList<string> AudioTags { get; init; } // Дубляж, MVO, AVO...
}
// ReleaseNameParser.Parse(string path, int maxYear) — maxYear передаётся снаружи (TimeProvider), парсер детерминирован
```
- вход — путь с разделителями `/` или `\`: используются имя файла и до двух родительских папок (папка сериала, папка сезона)
- шаги (по порядку): очистка мусора (расширение, хэш `[ABCD1234]`, `v2`, теги качества/озвучки) → нумерация серий (`SxxEyy`, `SxxEyy-Eyy`, `1x05`, `Серия 3`, `3 серия`, `Ep 05`, аниме `- 05`) → сезон из папки (`Season 2`, `Сезон 2`, `S02`, `2 сезон`) → год (`(2008)`, `.2008.`, без ложных срабатываний на `1080` и названия `1917`, `2012`; диапазон 1900..`maxYear`) → разделение двойного названия (`RU (EN)`, `RU.EN.2008` по смене алфавита, `RU _ EN`, `RU - EN`, полноширинный `／`) → нормализация разделителей (`.` `_` → пробел)

### Сопоставление
- `TitleNormalizer`: lower-case, `ё→е`, удаление пунктуации и артиклей (`the`, `a`), `&`→`and`; транслитерация НЕ используется (ищем по обоим названиям)
- `MatchScorer`: score ∈ [0..1] = 0.6·схожесть названия (максимум по парам «наше название × название/альтернативные названия кандидата», token-set + Левенштейн) + 0.3·год (точно = 1, ±1 = 0.5, нет года = 0.5) + 0.1·тип совпал
- порог по умолчанию `MinConfidence = 0.75`
- объединение: кандидаты из разных источников сливаются, если совпал любой ID (`imdb`, `tmdb`, `tvdb`, `kp`)

### Конфигурация (`PluginConfiguration`, XML-сериализуемая)
| Поле | По умолчанию | Назначение |
|---|---|---|
| `TmdbApiKey`, `KinopoiskApiKey`, `OmdbApiKey`, `TvdbApiKey`, `TvdbPin` | пусто | ключи; источник без ключа автоматически выключен |
| `EnableTmdb/Kinopoisk/Omdb/Tvdb` | true | ручное выключение |
| `PreferredLanguage` | `ru` | язык описаний/названий; влияет на приоритет текстовых полей |
| `MinConfidence` | 0.75 | порог сопоставления (ограничивается 0.5..1.0) |
| `CacheTtlHours` | 24 | TTL кэша ответов API |
| `AiEnabled` | false | включение ИИ-фоллбэка |
| `AiProvider` | `Anthropic` | `Anthropic` \| `OpenAiCompatible` |
| `AiEndpoint` | пусто | URL OpenAI-совместимого API (напр. `http://ollama:11434/v1`) |
| `AiApiKey` | пусто | ключ (не нужен для локальной модели) |
| `AiModel` | `claude-haiku-5-5` | для разбора имён достаточно быстрой дешёвой модели |
| `AiDailyRequestLimit` | 500 | защита от расходов |

Приоритет полей (v1 — **зашит в `FieldPriority`**, не настраивается; меняется только `PreferredLanguage`):
- название, описание, слоган: Кинопоиск (если язык `ru`) → TMDb → TVDB → OMDb
- `CommunityRating`: OMDb (IMDb) → Кинопоиск → TMDb; `CriticRating`: OMDb (Rotten Tomatoes)
- жанры, студии, люди: TMDb → Кинопоиск → TVDB
- сезоны и серии (названия, описания, даты, нумерация): TVDB → TMDb
- изображения: TMDb → TVDB → Кинопоиск

### Источники
- интерфейсы: `IMetadataSource` (поиск + детали фильма/сериала, внешние ID) — все 4 источника; `IEpisodeSource` (сезоны, серии, absolute-порядок) — только TMDb и TVDB
- **TMDb** v3: `/search/movie`, `/search/tv`, `/movie/{id}`, `/tv/{id}`, `/tv/{id}/season/{n}`, `/find/{imdb_id}`, `append_to_response=external_ids,credits,images`; ключ — v3 api_key или Bearer read access token
- **Кинопоиск** (kinopoisk.dev v1.4, заголовок `X-API-KEY`): `/v1.4/movie/search?query=`, `/v1.4/movie/{id}`; `externalId` (imdb/tmdb), `type` (movie/tv-series/cartoon/anime/animated-series); `selectFields` для экономии квоты
- **OMDb**: `?apikey=&i={imdb}` (основной режим — рейтинги по imdb ID) / `?t=&y=&type=` (последний фоллбэк поиска)
- **TVDB** v4: `POST /v4/login` (apikey + pin) → Bearer-токен (~1 месяц, в памяти, обновляется при 401); `/v4/search`, `/v4/series/{id}/extended`, `/v4/series/{id}/episodes/{season-type}` где `season-type` = `default` | `absolute`

### HTTP-слой (`SourceHttpClient`)
- именованный `HttpClient` через `IHttpClientFactory`, User-Agent `Jellyfin-SmartRecognizer/<version>`
- `IMemoryCache` сервера: ключ = источник + URL без секретов, TTL из конфигурации; отрицательные результаты — короткий TTL
- rate limiting на источник (token bucket; если `System.Threading.RateLimiting` недоступен без лишней DLL — `SemaphoreSlim` + минимальный интервал)
- повтор на 429/503 с учётом `Retry-After`, ожидание ограничено 10 с, максимум 2 попытки; прочие ошибки — лог + пустой результат, сканирование не падает
- исчерпанная суточная квота (Кинопоиск/OMDb) → источник выключается до конца суток
- ключи API никогда не попадают в логи и ключи кэша

### ИИ-фоллбэк (`AiReleaseRecognizer`)
- вход: имя файла + до двух родительских папок (не полный путь)
- выход: строгий JSON по схеме `{titles[], year?, kind, season?, episodes[], absoluteEpisode?}`
- Anthropic: Messages API, принудительный tool use со схемой; OpenAI-совместимый: `response_format: json_schema`
- `AiStateStore`: кэш ответов (по нормализованному пути, ≥ 7 дней) и суточный счётчик запросов хранятся в JSON-файле в папке данных плагина — переживают перезапуск сервера; таймаут запроса 15 с
- ⚠️ приватность: при облачном провайдере имена файлов уходят во внешний сервис — предупреждение на странице настроек

## What Goes Where
- **Implementation Steps** (`[ ]` checkboxes): код плагина, тесты, страница настроек, документация в репозитории
- **Post-Completion** (no checkboxes): полная ручная проверка в живом Jellyfin, получение API-ключей, публикация

## Implementation Steps

### Task 1: Scaffold solution, plugin skeleton and config accessor

**Files:**
- Create: `Jellyfin.Plugin.SmartRecognizer.sln`, `Directory.Build.props`, `build.yaml`, `.gitignore`, `.editorconfig`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Jellyfin.Plugin.SmartRecognizer.csproj`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Plugin.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/PluginServiceRegistrator.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Configuration/PluginConfiguration.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Configuration/IPluginConfigurationAccessor.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Jellyfin.Plugin.SmartRecognizer.Tests.csproj`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Configuration/PluginConfigurationTests.cs`

- [x] `git init`; определить актуальную стабильную версию Jellyfin — ✅ **12.4.0, net10.0, targetAbi 12.4.0.0**; зафиксировать версии `Jellyfin.Controller`/`Jellyfin.Model` в `Directory.Build.props`
- [x] сборки хоста — с `<ExcludeAssets>runtime</ExcludeAssets>`; проверить, доступен ли `System.Threading.RateLimiting` из общего фреймворка хоста (иначе — простой лимитер, см. Technical Details) — ✅ он в ASP.NET Core shared framework → `FrameworkReference Microsoft.AspNetCore.App`, DLL не копируется
- [x] включить `Nullable`, `TreatWarningsAsErrors`, анализаторы — ⚠️ вместо StyleCop/`AllEnabledByDefault` шаблона взят встроенный `AnalysisMode=Recommended` (меньше шума, те же ключевые правила)
- [x] write tests first: значения по умолчанию `PluginConfiguration`; источник без ключа считается выключенным (`IsSourceEnabled`); `MinConfidence` вне диапазона ограничивается
- [x] реализовать `Plugin` (Id GUID, Name, `IHasWebPages` — заглушка), `PluginConfiguration` (поля из Technical Details), `IPluginConfigurationAccessor` (читает `Plugin.Instance.Configuration` на каждом вызове), регистрацию accessor в `PluginServiceRegistrator`
- [x] `build.yaml` для jprm (name, guid, targetAbi, framework, artifacts)
- [x] run `dotnet build` + `dotnet test` — must pass before task 2

### Task 2: Spike — verify Jellyfin episode/season handling by providers

**Files:**
- Create: `docs/spikes/20261108-episode-resolution.md`

- [x] по исходникам Jellyfin целевой версии: учитываются ли `IndexNumber`/`ParentIndexNumber` из `MetadataResult<Episode>`, если встроенный резолвер их не распознал (`3 серия.mkv`)
- [x] может ли провайдер переопределить **неверное** ненулевое значение `IndexNumber`/`ParentIndexNumber` (`MergeData` обычно сохраняет существующие значения, кроме режима replace)
- [x] виртуальные сезоны создаются при обновлении сериала до запуска провайдеров серий: применится ли `ParentIndexNumber`, выставленный провайдером (аниме absolute → сезон), только на **следующем** скане? (от этого зависят Task 6 и Task 18)
- [x] распознаёт ли `SeasonPathParser` `Сезон 2` и форму `2 сезон`; создаётся ли элемент `Season`
- [x] получают ли lookup-объекты Season/Episode `SeriesProviderIds`, которые наш Series-провайдер выставил в том же скане
- [x] проверить сигнатуры `IExternalId` / `IExternalUrlProvider` в целевой версии (для Task 19)
- [x] решить: достаточно ли провайдеров или нужен `IItemResolver` / `ILibraryPostScanTask`; записать выводы в spike-документ
- [x] ➕ при необходимости добавить в план задачу на resolver (до Task 17) и обновить план — ✅ не нужен: провайдеров достаточно (см. spike-документ)
- [x] тесты не требуются (исследование без кода) — отметить в spike-документе

### Task 3: Parser — model and scene/torrent movie names

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Parsing/ParsedRelease.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Parsing/ReleaseNameParser.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Parsing/TokenDictionaries.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/TestData/release-names.json`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Parsing/ReleaseNameParserTests.cs`

- [x] write tests first: корпус ≥ 20 scene-имён фильмов (`The.Dark.Knight.2008.1080p.BluRay.x264-GROUP`, `Movie_Name_2019_WEB-DL`, `1917.2019.mkv`, `2012.2009.BDRip`, без года); ожидаются Titles, Year, Kind, Resolution, Source, ReleaseGroup; пути с `/` и с `\`
- [x] write tests for edge cases: пустая строка, только расширение, путь без файла, `1080` не год, год > `maxYear` или < 1900 не год (через фиксированный `maxYear`)
- [x] реализовать `MediaKind`, `ParsedRelease`, словари токенов качества/источника/кодеков, очистку и извлечение года (приватные методы `ReleaseNameParser`)
- [x] run tests - must pass before task 4

### Task 4: Parser — Russian release names

**Files:**
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/Parsing/ReleaseNameParser.cs`
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/Parsing/TokenDictionaries.cs`
- Modify: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/TestData/release-names.json`
- Modify: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Parsing/ReleaseNameParserTests.cs`

- [x] write tests first: ≥ 20 русских имён в том виде, в каком они лежат на диске — `Тёмный рыцарь (The Dark Knight) 2008 BDRip 1080p [Дубляж]`, `Темный.рыцарь.The.Dark.Knight.2008.BDRip`, `Тёмный рыцарь _ The Dark Knight (2008)`, `Тёмный рыцарь - The Dark Knight`, полноширинный `／`, `Брат 2 (2000) DVDRip`, `Иван Васильевич меняет профессию.1973.mkv`, теги `MVO/AVO/DUB/Лицензия/iTunes`
- [x] write tests for edge cases: скобки как часть названия, а не EN-вариант (`Мама (не) горюй`), дефис внутри названия (`Человек-паук`), `ё`/`е`, смешанные теги в `[]`
- [x] реализовать разделение двойного названия (скобки, смена алфавита, ` _ `, ` - `, `／`), словари русских тегов, заполнение `AudioTags`
- [x] run tests - must pass before task 5

### Task 5: Parser — episode numbering and season folders

**Files:**
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/Parsing/ReleaseNameParser.cs`
- Modify: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/TestData/release-names.json`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Parsing/EpisodeNumberingTests.cs`

- [x] write tests first: `Show.S01E02.WEB-DL`, `S01E01-E02`, `S01E01E02`, `1x05`, `Серия 3`, `3 серия`, `03.mkv` в папке `Сезон 2`, `Season 02/E05`, `2 сезон/05 серия`, specials `S00E01`; название сериала — из папки сериала, если в имени файла его нет; папка сериала без файла → `Kind = Series` (⚠️ по имени папку сериала не отличить от папки фильма, поэтому `Series` — только для папки сезона и сезонного пака `Show.S01.1080p`; в остальных случаях тип задаёт провайдер)
- [x] write tests for edge cases: фильм с числом в названии не становится серией (`Ocean's 11`, `Брат 2`), конфликт сезона в папке и в имени (имя файла приоритетнее), неоднозначность → `Kind = Unknown`
- [x] реализовать нумерацию и сезон из папки (до двух уровней родителей)
- [x] run tests - must pass before task 6

### Task 6: Parser — anime names and absolute numbering

**Files:**
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/Parsing/ReleaseNameParser.cs`
- Modify: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/TestData/release-names.json`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Parsing/AnimeNamesTests.cs`

- [x] write tests first: `[SubsPlease] Title - 05 (1080p) [ABCD1234].mkv`, `[Group] Title - 105v2`, `Title - 05 [BD 1080p]`, `Title S2 - 03`, `Title 2nd Season - 03`, `Title - 05.5` (спешл), батч-папка `[Group] Title (01-12)`
- [x] write tests for edge cases: CRC-хэш и `v2` не попадают в название/номер, `- 2019` не серия, аниме с `SxxEyy` использует явный сезон
- [x] реализовать: группа релиза из ведущих `[]`, `AbsoluteEpisode`, сезон из `S2`/`2nd Season`
- [x] run tests - must pass before task 7

### Task 7: Title normalization and match scoring

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Matching/TitleNormalizer.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Matching/MatchScorer.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Matching/TitleNormalizerTests.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Matching/MatchScorerTests.cs`

- [x] write tests first: нормализация (`Тёмный рыцарь` = `Темный Рыцарь`, `The Lord of the Rings: The Fellowship` vs `Lord of the Rings Fellowship`, `&`/`and`); scoring — точное совпадение + год ≈ 1.0, год ±1 и другой тип снижают
- [x] write tests for edge cases: пустые названия кандидата, нет года ни у кого, короткие названия (`Up`, `Он`) не дают ложных высоких совпадений
- [x] реализовать `TitleNormalizer` и `MatchScorer` по формуле из Technical Details
- [x] run tests - must pass before task 8

### Task 8: Source interfaces and shared HTTP layer

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Sources/IMetadataSource.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Sources/IEpisodeSource.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Sources/SourceModels.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Sources/SourceHttpClient.cs`
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/PluginServiceRegistrator.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Sources/FakeHttpMessageHandler.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Sources/SourceHttpClientTests.cs`

- [x] write tests first: повторный запрос из кэша; 429 с `Retry-After` → повтор и успех; 500 → пустой результат без исключения; отмена через `CancellationToken`; секрет не попадает в ключ кэша
- [x] write tests for edge cases: `Retry-After` больше 10 с → не ждём, пустой результат; битый JSON → пустой результат + лог; превышение лимита попыток; отрицательный кэш с коротким TTL; квота исчерпана → источник выключен до конца суток (через `TimeProvider`)
- [x] определить `IMetadataSource` (поиск, детали фильма/сериала), `IEpisodeSource` (сезон, серии, absolute-порядок), общие модели с внешними ID
- [x] реализовать `SourceHttpClient` (кэш, лимитер, retry), регистрацию `HttpClient` в DI
- [x] run tests - must pass before task 9

### Task 9: TMDb source client

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Sources/Tmdb/TmdbSource.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Sources/Tmdb/TmdbDtos.cs`
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/PluginServiceRegistrator.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/TestData/tmdb/*.json`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Sources/TmdbSourceTests.cs`

- [x] write tests first (фикстуры): поиск фильма/сериала с годом и языком, детали с `external_ids`, сезон с сериями, `find` по imdb ID, изображения
- [x] write tests for edge cases: пустой поиск, 401 (неверный ключ) → лог без ключа, отсутствующие поля
- [x] реализовать `TmdbSource : IMetadataSource, IEpisodeSource` (api_key или Bearer, язык из конфигурации, базовый URL картинок — ⚠️ константа `image.tmdb.org/t/p/original` вместо запроса `/configuration` (URL стабилен годами, минус лишний запрос)); зарегистрировать в DI
- [x] run tests - must pass before task 10

### Task 10: Kinopoisk source client

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Sources/Kinopoisk/KinopoiskSource.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Sources/Kinopoisk/KinopoiskDtos.cs`
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/PluginServiceRegistrator.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/TestData/kinopoisk/*.json`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Sources/KinopoiskSourceTests.cs`

- [x] write tests first: поиск по русскому названию; маппинг `type` (movie/tv-series/cartoon/anime/animated-series) в `MediaKind`; `externalId` → imdb/tmdb; рейтинги kp/imdb; альтернативные названия (`alternativeName`, `names[]`)
- [x] write tests for edge cases: исчерпана суточная квота (403/429) → источник выключен до конца суток без повторов; сериал без данных о сериях
- [x] реализовать `KinopoiskSource : IMetadataSource` (`X-API-KEY`; ⚠️ `selectFields` не используется: квота считается по запросам, а не по объёму ответа); зарегистрировать в DI
- [x] run tests - must pass before task 11

### Task 11: OMDb source client

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Sources/Omdb/OmdbSource.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Sources/Omdb/OmdbDtos.cs`
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/PluginServiceRegistrator.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/TestData/omdb/*.json`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Sources/OmdbSourceTests.cs`

- [x] write tests first: lookup по imdb ID, поиск по названию + году + типу, разбор рейтингов IMDb (`8.9/10`), RT (`94%`), Metacritic
- [x] write tests for edge cases: `"Response":"False"` с `Error`, `"N/A"` → null, лимит запросов
- [x] реализовать `OmdbSource : IMetadataSource`; зарегистрировать в DI
- [x] run tests - must pass before task 12

### Task 12: TVDB source client

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Sources/Tvdb/TvdbSource.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Sources/Tvdb/TvdbTokenProvider.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Sources/Tvdb/TvdbDtos.cs`
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/PluginServiceRegistrator.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/TestData/tvdb/*.json`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Sources/TvdbSourceTests.cs`

- [x] write tests first: логин и кэширование токена, повторный логин при 401, поиск сериала, `series/{id}/extended`, серии в порядке `default` и `absolute`, переводы (`translations/rus`)
- [x] write tests for edge cases: неверный ключ/PIN, параллельные запросы не логинятся дважды, сериал без absolute-порядка
- [x] реализовать `TvdbTokenProvider` и `TvdbSource : IMetadataSource, IEpisodeSource`; зарегистрировать в DI (⚠️ только сериалы; названия/описания сезонов берутся из TMDb — в TVDB их почти нет на русском)
- [x] run tests - must pass before task 13

### Task 13: Candidate resolver — staged, quota-aware search

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Matching/CandidateResolver.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Matching/ResolvedMatch.cs`
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/PluginServiceRegistrator.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Matching/CandidateResolverTests.cs`

- [x] write tests first (моки источников): порядок этапов TMDb → Кинопоиск (только кириллица или язык `ru`) → TVDB (только сериалы) → OMDb; **Кинопоиск/OMDb не вызываются, если TMDb уже дал уверенное совпадение**; лучший кандидат ≥ порога → `ResolvedMatch` со всеми известными ID
- [x] write tests for edge cases: все кандидаты ниже порога → нет совпадения; одинаковые названия с разными годами; источник бросил исключение — следующий этап продолжается; выключенный источник пропускается
- [x] реализовать `CandidateResolver`; зарегистрировать в DI
- [x] run tests - must pass before task 14

### Task 14: Metadata merger with fixed field priority

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Aggregation/FieldPriority.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Aggregation/MetadataMerger.cs`
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/PluginServiceRegistrator.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Aggregation/MetadataMergerTests.cs`

- [ ] write tests first: слияние для `Movie`, `Series`, `Season`, `Episode` по приоритетам из Technical Details (описание из Кинопоиска при `ru`, `CommunityRating` из OMDb, жанры из TMDb, данные сезона из TVDB); пропуск пустых полей с переходом к следующему источнику
- [ ] write tests for edge cases: язык ≠ `ru` (Кинопоиск не первый для текстов), все источники пустые по полю, `ProviderIds` и `People` объединяются без дубликатов
- [ ] реализовать `FieldPriority` (зашитые порядки, зависят только от `PreferredLanguage`) и `MetadataMerger`; зарегистрировать в DI
- [ ] run tests - must pass before task 15

### Task 15: Recognition pipeline (without AI) and DI wiring test

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Matching/RecognitionPipeline.cs`
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/PluginServiceRegistrator.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Matching/RecognitionPipelineTests.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/DependencyInjectionTests.cs`

- [ ] write tests first: известные `ProviderIds` → поиск пропускается; путь → парсер → resolver → **догрузка деталей по ID** во всех включённых источниках параллельно (OMDb — по imdb ID, серии — через `IEpisodeSource`) → merger
- [ ] write tests for edge cases: детали одного источника упали — остальные сливаются; нет совпадения → пустой результат; nullable-хук фоллбэка (`IReleaseFallback?`) вызывается только при отсутствии совпадения, его подсказка идёт через тот же resolver и порог
- [ ] write DI wiring test: `ServiceCollection` + `PluginServiceRegistrator` → все зарегистрированные сервисы создаются (провайдеры добавят себя в этот тест в задачах 16–19)
- [ ] реализовать `RecognitionPipeline`; зарегистрировать в DI
- [ ] run tests - must pass before task 16

### Task 16: Movie metadata provider + early smoke test

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Providers/MovieProvider.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Providers/MovieProviderTests.cs`
- Modify: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/DependencyInjectionTests.cs`

- [ ] write tests first: `GetMetadata` по пути русского/scene-релиза → заполненный `Movie` с `ProviderIds` и `HasMetadata = true`; `GetSearchResults` для «Identify» (по `Name`/`Year` от пользователя) возвращает список с оценками; lookup по известному ID
- [ ] write tests for edge cases: нет совпадения → `HasMetadata = false`; все источники выключены; отмена
- [ ] реализовать `MovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder` (без регистрации в DI — Jellyfin найдёт его сам); добавить в DI wiring test
- [ ] smoke-тест вручную: собрать плагин, загрузить в Docker Jellyfin целевой версии, провайдер виден в настройках библиотеки, один фильм распознан; расхождения API — записать ⚠️ и исправить план
- [ ] run tests - must pass before task 17

### Task 17: Series and season metadata providers

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Providers/SeriesProvider.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Providers/SeasonProvider.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Providers/SeriesProviderTests.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Providers/SeasonProviderTests.cs`
- Modify: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/DependencyInjectionTests.cs`

- [ ] write tests first: сериал распознаётся по имени папки (в т.ч. аниме-батч `[Group] Title (01-12)`); сезон получает название/описание/дату по `SeriesProviderIds` + номеру сезона
- [ ] write tests for edge cases: сезон без номера, сезон 0 (specials), сериал найден только в Кинопоиске — заполняются доступные поля
- [ ] реализовать `SeriesProvider` и `SeasonProvider` (по выводам Task 2); добавить в DI wiring test
- [ ] run tests - must pass before task 18

### Task 18: Episode metadata provider with absolute numbering

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Providers/EpisodeProvider.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Providers/EpisodeProviderTests.cs`
- Modify: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/DependencyInjectionTests.cs`

- [ ] write tests first: серия по `SeriesProviderIds` + сезон/номер; если Jellyfin не распознал номер (`IndexNumber == null`) — номер из собственного парсинга пути; аниме `AbsoluteEpisode` → сезон/серия через TVDB absolute-порядок (фоллбэк — TMDb)
- [ ] write tests for edge cases: мультисерийный файл `S01E01-E02` (`IndexNumberEnd`), серии нет в источниках, absolute-номер больше числа серий
- [ ] реализовать `EpisodeProvider : IRemoteMetadataProvider<Episode, EpisodeInfo>` (поведение номеров — по выводам Task 2); добавить в DI wiring test
- [ ] run tests - must pass before task 19

### Task 19: Image provider and Kinopoisk external ID/URL

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Providers/SmartImageProvider.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Providers/KinopoiskExternalId.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Providers/KinopoiskExternalUrlProvider.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Providers/SmartImageProviderTests.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Providers/KinopoiskExternalIdTests.cs`
- Modify: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/DependencyInjectionTests.cs`

- [ ] write tests first: изображения Movie/Series по своим `ProviderIds`, Season/Episode — по ID **родительского сериала** + номера сезона/серии; типы `Primary`, `Backdrop`, `Logo`, `Thumb`; порядок источников TMDb → TVDB → Кинопоиск; язык постеров (`ru` → без языка → `en`)
- [ ] write tests first: `KinopoiskExternalId` поддерживает Movie и Series; URL — `/film/{id}/` для фильма и `/series/{id}/` для сериала
- [ ] write tests for edge cases: нет ID ни одного источника → пустой список; дубликаты URL; элемент без родителя
- [ ] реализовать `SmartImageProvider : IRemoteImageProvider`, `KinopoiskExternalId : IExternalId` и `KinopoiskExternalUrlProvider : IExternalUrlProvider` (сигнатуры — по целевой версии из Task 2); добавить в DI wiring test
- [ ] run tests - must pass before task 20

### Task 20: Plugin configuration page

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Configuration/configPage.html`
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/Plugin.cs`
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/Jellyfin.Plugin.SmartRecognizer.csproj`
- Modify: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Configuration/PluginConfigurationTests.cs`

- [ ] write tests first: `GetPages()` возвращает встроенный ресурс, ресурс есть в сборке; XML-сериализация/десериализация `PluginConfiguration` (через `XmlSerializer`) сохраняет все поля
- [ ] write tests for edge cases: старый конфиг без новых полей загружается с дефолтами
- [ ] реализовать страницу (компоненты Jellyfin dashboard: ключи как `type=password`, флажки источников, язык, порог, TTL), подключить как `EmbeddedResource`
- [ ] run tests - must pass before task 21

### Task 21: LLM clients (Anthropic and OpenAI-compatible)

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Ai/ILlmClient.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Ai/AnthropicLlmClient.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Ai/OpenAiCompatibleLlmClient.cs`
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/PluginServiceRegistrator.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/TestData/ai/*.json`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Ai/AnthropicLlmClientTests.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Ai/OpenAiCompatibleLlmClientTests.cs`

- [ ] write tests first (фейковый handler): Anthropic — запрос содержит tool со схемой и принудительный `tool_choice`, ответ `tool_use` → JSON; OpenAI-совместимый — запрос содержит `response_format: json_schema`, ответ → JSON; ключ передаётся в правильном заголовке; без ключа (локальная модель) заголовок не отправляется
- [ ] write tests for edge cases: таймаут 15 с, 429/5xx, ответ без tool_use / пустой content → null, ключ не попадает в логи
- [ ] реализовать `ILlmClient` (`Task<JsonDocument?> ExtractAsync(prompt, schema, ct)`) и обе реализации; выбор реализации по `AiProvider`; зарегистрировать в DI
- [ ] run tests - must pass before task 22

### Task 22: AI fallback recognizer plugged into the pipeline

**Files:**
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Ai/AiReleaseRecognizer.cs`
- Create: `src/Jellyfin.Plugin.SmartRecognizer/Ai/AiStateStore.cs`
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/PluginServiceRegistrator.cs`
- Modify: `src/Jellyfin.Plugin.SmartRecognizer/Configuration/configPage.html`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Ai/AiReleaseRecognizerTests.cs`
- Create: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Ai/AiStateStoreTests.cs`
- Modify: `tests/Jellyfin.Plugin.SmartRecognizer.Tests/Matching/RecognitionPipelineTests.cs`

- [ ] write tests first: корректный JSON → `ParsedRelease`; отправляется только имя файла + 2 родительские папки; ответ кэшируется в `AiStateStore` и переживает пересоздание store (файл во временной папке теста); суточный счётчик сохраняется в файл и сбрасывается при смене суток (`TimeProvider`)
- [ ] write tests for edge cases: невалидный/неполный JSON → нет подсказки; лимит исчерпан → LLM не вызывается; ИИ выключен или нет ключа/endpoint → фоллбэк не регистрируется; ID от ИИ игнорируются; повреждённый файл состояния → начинаем с пустого
- [ ] реализовать `AiReleaseRecognizer : IReleaseFallback` (промпт с примерами из корпуса, JSON-схема = поля `ParsedRelease`) и `AiStateStore` (JSON в папке данных плагина); зарегистрировать как хук pipeline
- [ ] добавить на страницу настроек блок «ИИ» (провайдер, endpoint, ключ, модель, лимит, предупреждение о приватности); обновить тест pipeline: ИИ не вызывается при уверенном совпадении правил
- [ ] run tests - must pass before task 23

### Task 23: Verify acceptance criteria
- [ ] verify all requirements from Overview are implemented: 4 типа имён распознаются, 4 источника работают, `ProviderIds` проставляются
- [ ] verify поэтапный поиск: на корпусе фильмов Кинопоиск/OMDb вызываются только когда нужно (подсчёт вызовов в тестах pipeline)
- [ ] verify ИИ-фоллбэк: вызывается только при неудаче правил, отключён без ключа/endpoint, соблюдает суточный лимит после перезапуска
- [ ] verify edge cases: нет ключей, недоступный источник, исчерпанная квота, низкая уверенность → нет совпадения
- [ ] run full test suite: `dotnet test`
- [ ] e2e tests: UI e2e-тестов в проекте нет — пропускается (ручная проверка в Post-Completion)
- [ ] verify test coverage: `dotnet test --collect:"XPlat Code Coverage"`; цель ≥ 80% для `Parsing/` и `Matching/`
- [ ] `dotnet build -c Release` без предупреждений; jprm-zip содержит только DLL плагина (без `Jellyfin.*`, `Microsoft.Extensions.*` и прочих сборок хоста)

### Task 24: [Final] Update documentation
- [ ] создать README.md (+ из spike: неверный ненулевой номер серии исправляется через «Replace all metadata»; аниме absolute → сезон применяется со второго обновления сериала): назначение, установка (репозиторий плагинов / вручную), получение API-ключей, настройка порядка провайдеров в библиотеке (**отключить или опустить встроенные TMDb/OMDb**, иначе они подставят наивное совпадение там, где наш провайдер консервативно ничего не нашёл), ИИ-фоллбэк и приватность, примеры поддерживаемых имён
- [ ] создать CLAUDE.md: структура проекта, команды сборки/тестов, правило «каждое новое имя-кейс — в корпус `release-names.json`», «провайдеры не регистрировать в DI»
- [ ] move this plan to `docs/plans/completed/`

## Post-Completion
*Items requiring manual intervention or external systems - no checkboxes, informational only*

**Manual verification**:
- Docker Jellyfin целевой версии, тестовая библиотека из файлов корпуса (пустые `.mkv`-заглушки достаточны)
- проверить: сканирование, «Identify», ссылки на Кинопоиск/IMDb/TMDb/TVDB, постеры, русские описания, сезоны/серии аниме
- скан большой библиотеки (1000+ элементов): укладывается ли в квоты API, нет ли зависаний; доля файлов, дошедших до ИИ-фоллбэка
- безопасность: ключи API хранятся в XML-конфиге плагина в открытом виде (стандартная практика Jellyfin) — убедиться, что они не попадают в логи

**External system updates**:
- API-ключи: TMDb (бесплатно), kinopoisk.dev (бесплатный тариф с суточной квотой), OMDb (1000/день бесплатно или Patreon), TVDB v4 (платная подписка или лицензия проекта)
- ИИ: ключ Anthropic API или локальная модель (Ollama); оценить расходы на реальной библиотеке
- ⚠️ kinopoisk.dev — неофициальный API: проверить условия использования и стабильность перед продакшеном
- лицензия: плагин линкуется со сборками Jellyfin (GPL) — выбрать GPL-совместимую лицензию до публикации
- публикация: GitHub-репозиторий + GitHub Actions (jprm build) + `manifest.json` для репозитория плагинов
- при обновлении Jellyfin до новой мажорной версии — пересобрать под новый `targetAbi`
