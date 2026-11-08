using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartRecognizer.Configuration;
using Jellyfin.Plugin.SmartRecognizer.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartRecognizer.Aggregation;

/// <summary>
/// Combines details from several sources into Jellyfin items: for every field the first
/// non-empty value in <see cref="FieldPriority"/> order wins.
/// </summary>
public static class MetadataMerger
{
    /// <summary>Builds a movie or series result.</summary>
    public static MetadataResult<T> Merge<T>(IReadOnlyList<SourceDetails> details, string language)
        where T : BaseItem, new()
    {
        var result = new MetadataResult<T> { Item = new T() };
        if (details.Count == 0)
        {
            return result;
        }

        var item = result.Item;
        string? Text(Func<SourceDetails, string?> field) => First(details, FieldGroup.Text, language, d => Clean(field(d)));
        TValue? Classify<TValue>(Func<SourceDetails, TValue?> field)
            where TValue : struct => FirstValue(details, FieldGroup.Classification, language, field);

        item.Name = Text(d => d.Title)!;
        item.OriginalTitle = Text(d => d.OriginalTitle);
        item.Overview = Text(d => d.Overview);
        item.Tagline = Text(d => d.Tagline);
        item.PremiereDate = Classify(d => d.PremiereDate);
        item.ProductionYear = Classify(d => d.Year) ?? item.PremiereDate?.Year;
        item.EndDate = Classify(d => d.EndDate);
        item.CommunityRating = FirstValue(details, FieldGroup.CommunityRating, language, d => d.CommunityRating);
        item.CriticRating = FirstValue(details, FieldGroup.CriticRating, language, d => d.CriticRating);
        item.OfficialRating = First(details, FieldGroup.Classification, language, d => Clean(d.OfficialRating));
        item.Genres = FirstList(details, language, d => d.Genres);
        item.Studios = FirstList(details, language, d => d.Studios);
        if (Classify(d => d.RuntimeMinutes) is { } minutes)
        {
            item.RunTimeTicks = TimeSpan.FromMinutes(minutes).Ticks;
        }

        var ids = details.Aggregate(ProviderIdSet.None, (acc, d) => acc.Merge(d.Ids));
        foreach (var (key, value) in ids.ToDictionary())
        {
            item.SetProviderId(key, value);
        }

        // People differ by language and spelling between sources: take one source's list.
        var people = Ordered(details, FieldGroup.Classification, language).FirstOrDefault(d => d.People.Count > 0)?.People ?? [];
        var order = 0;
        foreach (var person in people)
        {
            result.AddPerson(new PersonInfo
            {
                Name = person.Name,
                Role = person.Character ?? string.Empty,
                Type = ToKind(person.Role),
                ImageUrl = person.ImageUrl ?? string.Empty,
                SortOrder = person.Role == SourcePersonRole.Actor ? order++ : null,
            });
        }

        result.HasMetadata = !string.IsNullOrWhiteSpace(item.Name);
        return result;
    }

    /// <summary>Fills a season from its sources; false when none had anything.</summary>
    public static bool MergeSeason(IReadOnlyList<SourceSeason> seasons, Season item)
    {
        var ordered = seasons.OrderBy(s => FieldPriority.Rank(FieldGroup.Episodes, string.Empty, s.Source)).ToList();
        var name = ordered.Select(s => Clean(s.Name)).FirstOrDefault(v => v is not null);
        var overview = ordered.Select(s => Clean(s.Overview)).FirstOrDefault(v => v is not null);
        var premiere = ordered.Select(s => s.PremiereDate).FirstOrDefault(v => v is not null);
        if (name is null && overview is null && premiere is null)
        {
            return false;
        }

        item.Name = name ?? item.Name;
        item.Overview = overview;
        item.PremiereDate = premiere;
        item.ProductionYear = premiere?.Year;
        item.IndexNumber ??= ordered.FirstOrDefault()?.SeasonNumber;
        return true;
    }

    /// <summary>Fills an episode from its sources (TVDB first, then TMDb); false when none had anything.</summary>
    public static bool MergeEpisode(IReadOnlyList<SourceEpisode> episodes, Episode item)
    {
        if (episodes.Count == 0)
        {
            return false;
        }

        var ordered = episodes.OrderBy(e => FieldPriority.Rank(FieldGroup.Episodes, string.Empty, e.Source)).ToList();
        T? FirstOf<T>(Func<SourceEpisode, T?> field)
            where T : struct => ordered.Select(field).FirstOrDefault(v => v is not null);

        item.Name = ordered.Select(e => Clean(e.Name)).FirstOrDefault(v => v is not null)!;
        item.Overview = ordered.Select(e => Clean(e.Overview)).FirstOrDefault(v => v is not null);
        item.PremiereDate = FirstOf(e => e.PremiereDate);
        item.ProductionYear = item.PremiereDate?.Year;
        item.ParentIndexNumber = FirstOf(e => e.SeasonNumber);
        item.IndexNumber = FirstOf(e => e.EpisodeNumber);
        item.CommunityRating = FirstOf(e => e.CommunityRating);
        if (FirstOf(e => e.RuntimeMinutes) is { } minutes)
        {
            item.RunTimeTicks = TimeSpan.FromMinutes(minutes).Ticks;
        }

        var ids = ordered.Aggregate(ProviderIdSet.None, (acc, e) => acc.Merge(e.Ids));
        foreach (var (key, value) in ids.ToDictionary())
        {
            item.SetProviderId(key, value);
        }

        return item.Name is not null || item.Overview is not null || item.PremiereDate is not null;
    }

    /// <summary>
    /// Image order: source priority, then language (preferred → none → English → other), then rating.
    /// Duplicate URLs are dropped.
    /// </summary>
    public static List<SourceImage> OrderImages(IEnumerable<SourceImage> images, Func<SourceImage, MetadataSourceKind> sourceOf, string language)
    {
        var preferred = language.Split('-')[0];
        int LanguageRank(string? lang) =>
            lang is null ? 1
            : string.Equals(lang, preferred, StringComparison.OrdinalIgnoreCase) ? 0
            : string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? 2
            : 3;

        return images
            .OrderBy(i => FieldPriority.Rank(FieldGroup.Images, language, sourceOf(i)))
            .ThenBy(i => LanguageRank(i.Language))
            .ThenByDescending(i => i.Rating ?? 0)
            .DistinctBy(i => i.Url, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<SourceDetails> Ordered(IReadOnlyList<SourceDetails> details, FieldGroup group, string language) =>
        details.OrderBy(d => FieldPriority.Rank(group, language, d.Source));

    private static string? First(IReadOnlyList<SourceDetails> details, FieldGroup group, string language, Func<SourceDetails, string?> field) =>
        Ordered(details, group, language).Select(field).FirstOrDefault(v => v is not null);

    private static TValue? FirstValue<TValue>(IReadOnlyList<SourceDetails> details, FieldGroup group, string language, Func<SourceDetails, TValue?> field)
        where TValue : struct =>
        Ordered(details, group, language)
            .Where(d => FieldPriority.Rank(group, language, d.Source) < FieldPriority.For(group, language).Count)
            .Select(field)
            .FirstOrDefault(v => v is not null);

    private static string[] FirstList(IReadOnlyList<SourceDetails> details, string language, Func<SourceDetails, IReadOnlyList<string>> field) =>
        Ordered(details, FieldGroup.Classification, language).Select(field).FirstOrDefault(l => l.Count > 0)?.ToArray() ?? [];

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PersonKind ToKind(SourcePersonRole role) => role switch
    {
        SourcePersonRole.Director => PersonKind.Director,
        SourcePersonRole.Writer => PersonKind.Writer,
        SourcePersonRole.Producer => PersonKind.Producer,
        SourcePersonRole.Composer => PersonKind.Composer,
        _ => PersonKind.Actor,
    };
}
