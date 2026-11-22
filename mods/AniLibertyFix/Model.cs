using System.Collections.Generic;

namespace AniLiberty;

public class Name
{
    public string main { get; set; }

    public string english { get; set; }

    public string alternative { get; set; }
}

public class Poster
{
    public string src { get; set; }
}

public class SearchItem
{
    public int id { get; set; }

    public int year { get; set; }

    public Name name { get; set; }

    public Poster poster { get; set; }
}

public class Episode
{
    public string ordinal { get; set; }

    public string name { get; set; }

    public string hls_1080 { get; set; }

    public string hls_720 { get; set; }

    public string hls_480 { get; set; }
}

public class ReleaseType
{
    public string value { get; set; }
}

public class Release
{
    public int id { get; set; }

    public string alias { get; set; }

    public ReleaseType type { get; set; }

    public Name name { get; set; }

    public Episode[] episodes { get; set; }
}

// GET /anime/franchises/release/{id} -> массив франшиз
public class FranchiseRelease
{
    public int sort_order { get; set; }

    public int release_id { get; set; }

    public Release release { get; set; }
}

public class Franchise
{
    public List<FranchiseRelease> franchise_releases { get; set; }
}

// Релиз + номер сезона, вычисленный по франшизе (кешируется вместе)
public class ReleaseData
{
    public Release release { get; set; }

    // 0 - по франшизе определить не удалось, используется GetSeason
    public int season { get; set; }
}
