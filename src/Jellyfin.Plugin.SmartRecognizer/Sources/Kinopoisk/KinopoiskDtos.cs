namespace Jellyfin.Plugin.SmartRecognizer.Sources.Kinopoisk;

#pragma warning disable CA1812 // Instantiated by System.Text.Json.

internal sealed class KpList
{
    public List<KpMovie> Docs { get; set; } = [];
}

internal sealed class KpExternalId
{
    public string? Imdb { get; set; }

    public long? Tmdb { get; set; }
}

internal sealed class KpName
{
    public string? Name { get; set; }
}

internal sealed class KpRating
{
    public double? Kp { get; set; }

    public double? Imdb { get; set; }
}

internal sealed class KpImage
{
    public string? Url { get; set; }
}

internal sealed class KpPremiere
{
    public string? World { get; set; }

    public string? Russia { get; set; }
}

internal sealed class KpPerson
{
    public string? Photo { get; set; }

    public string? Name { get; set; }

    public string? EnName { get; set; }

    public string? Description { get; set; }

    public string? EnProfession { get; set; }
}

internal sealed class KpMovie
{
    public long Id { get; set; }

    public KpExternalId? ExternalId { get; set; }

    public string? Name { get; set; }

    public string? AlternativeName { get; set; }

    public string? EnName { get; set; }

    public List<KpName> Names { get; set; } = [];

    public string? Type { get; set; }

    public bool? IsSeries { get; set; }

    public int? Year { get; set; }

    public string? Description { get; set; }

    public string? ShortDescription { get; set; }

    public string? Slogan { get; set; }

    public KpRating? Rating { get; set; }

    public int? AgeRating { get; set; }

    public string? RatingMpaa { get; set; }

    public int? MovieLength { get; set; }

    public int? SeriesLength { get; set; }

    public List<KpName> Genres { get; set; } = [];

    public List<KpName> ProductionCompanies { get; set; } = [];

    public KpPremiere? Premiere { get; set; }

    public List<KpPerson> Persons { get; set; } = [];

    public KpImage? Poster { get; set; }

    public KpImage? Backdrop { get; set; }

    public KpImage? Logo { get; set; }
}
