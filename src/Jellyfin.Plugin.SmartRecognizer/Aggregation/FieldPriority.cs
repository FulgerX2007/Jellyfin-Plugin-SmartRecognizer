using Jellyfin.Plugin.SmartRecognizer.Configuration;

namespace Jellyfin.Plugin.SmartRecognizer.Aggregation;

public enum FieldGroup
{
    /// <summary>Title, overview, tagline.</summary>
    Text,

    CommunityRating,

    CriticRating,

    /// <summary>Genres, studios, people, dates, runtime, age rating.</summary>
    Classification,

    /// <summary>Season and episode data.</summary>
    Episodes,

    Images,
}

/// <summary>
/// Which source wins for each group of fields. Fixed in v1; only the language changes it.
/// </summary>
public static class FieldPriority
{
    private static readonly MetadataSourceKind[] RussianText = [MetadataSourceKind.Kinopoisk, MetadataSourceKind.Tmdb, MetadataSourceKind.Tvdb, MetadataSourceKind.Omdb];
    private static readonly MetadataSourceKind[] OtherText = [MetadataSourceKind.Tmdb, MetadataSourceKind.Tvdb, MetadataSourceKind.Omdb, MetadataSourceKind.Kinopoisk];
    private static readonly MetadataSourceKind[] Community = [MetadataSourceKind.Omdb, MetadataSourceKind.Kinopoisk, MetadataSourceKind.Tmdb, MetadataSourceKind.Tvdb];
    private static readonly MetadataSourceKind[] Critic = [MetadataSourceKind.Omdb];
    private static readonly MetadataSourceKind[] Classification = [MetadataSourceKind.Tmdb, MetadataSourceKind.Kinopoisk, MetadataSourceKind.Tvdb, MetadataSourceKind.Omdb];
    private static readonly MetadataSourceKind[] Episodes = [MetadataSourceKind.Tvdb, MetadataSourceKind.Tmdb];
    private static readonly MetadataSourceKind[] Images = [MetadataSourceKind.Tmdb, MetadataSourceKind.Tvdb, MetadataSourceKind.Kinopoisk, MetadataSourceKind.Omdb];

    public static IReadOnlyList<MetadataSourceKind> For(FieldGroup group, string language) => group switch
    {
        FieldGroup.Text => IsRussian(language) ? RussianText : OtherText,
        FieldGroup.CommunityRating => Community,
        FieldGroup.CriticRating => Critic,
        FieldGroup.Classification => Classification,
        FieldGroup.Episodes => Episodes,
        FieldGroup.Images => Images,
        _ => Classification,
    };

    /// <summary>Position of a source in a group; sources not listed go last.</summary>
    public static int Rank(FieldGroup group, string language, MetadataSourceKind source)
    {
        var order = For(group, language);
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == source)
            {
                return i;
            }
        }

        return order.Count;
    }

    private static bool IsRussian(string language) => language.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
}
