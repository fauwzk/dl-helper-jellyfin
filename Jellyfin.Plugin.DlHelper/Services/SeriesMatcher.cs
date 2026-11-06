using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DlHelper.Services;

/// <summary>Où ranger un téléchargement de série : dossier de la série + dossier de saison.</summary>
public class SeriesPlacement
{
    /// <summary>Nom du dossier de la série dans la destination (existant ou à créer).</summary>
    public string Show { get; set; } = string.Empty;

    public bool ShowExists { get; set; }

    public int? Season { get; set; }

    /// <summary>Dossier de saison dans la série (null = directement dans le dossier de la série).</summary>
    public string? SeasonFolder { get; set; }

    public bool SeasonExists { get; set; }

    /// <summary>Épisodes trouvés dans le téléchargement.</summary>
    public List<int> Episodes { get; set; } = new();

    /// <summary>Épisodes déjà présents dans le dossier de saison.</summary>
    public List<int> AlreadyThere { get; set; } = new();

    /// <summary>Nombre de vidéos déjà présentes dans le dossier de saison.</summary>
    public int ExistingVideos { get; set; }

    public string RelativeTarget => SeasonFolder is null ? Show : Path.Combine(Show, SeasonFolder);
}

/// <summary>Reconnaît « Titre S03 », « Titre S03E05 », « [Groupe] Titre - 12 »… et retrouve la série dans la destination.</summary>
public static class SeriesMatcher
{
    private static readonly Regex EpisodeRe = new(
        @"(?<![a-z0-9])s(?<s>\d{1,2})[ ._-]?e(?<e>\d{1,3})(?:[-_ ]?e?(?<e2>\d{1,3}))?(?![0-9])|(?<![a-z0-9])(?<s>\d{1,2})x(?<e>\d{2,3})(?![0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SeasonRe = new(
        @"(?<![a-z0-9])(?:s(?<s>\d{1,2})(?![0-9e])|(?:saison|season|staffel|temporada)[ ._-]*(?<s>\d{1,2})(?![0-9]))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Numérotation absolue des animes : « Titre - 12 », « Titre - 012v2 ».</summary>
    private static readonly Regex AbsoluteRe = new(@"\s-\s(?<e>\d{1,4})(?:v\d)?(?![0-9])", RegexOptions.Compiled);

    private static readonly Regex GroupRe = new(@"^\s*(\[[^\]]*\]|\([^)]*\))\s*", RegexOptions.Compiled);

    private static readonly Regex YearRe = new(@"[\(\[]?\b(19|20)\d{2}\b[\)\]]?", RegexOptions.Compiled);

    /// <summary>Ce qui suit le titre dans un nom de release et ne fait pas partie du titre.</summary>
    private static readonly Regex JunkRe = new(
        @"\b(integrale|intégrale|complete|multi|vostfr|french|truefrench|vff|vf|1080p|720p|2160p|4k|web|webrip|web-dl|bluray|x264|x265|hevc)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Dossier de saison : « S03 », « Season 3 », « Season.03 », « Saison 3 »…</summary>
    private static readonly Regex SeasonFolderRe = new(
        @"^(?<prefix>s|season|saison|staffel|temporada)(?<sep>[ ._-]*)(?<num>\d{1,3})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public record Parsed(string Title, int? Season, List<int> Episodes);

    /// <summary>Titre + saison + épisodes d'après le nom (et le contenu pour un dossier). null = pas une série.</summary>
    public static Parsed? Parse(string name, IEnumerable<string>? videoFiles = null)
    {
        var baseName = Path.HasExtension(name) && FileOps.VideoExt.Contains(Path.GetExtension(name)) ? Path.GetFileNameWithoutExtension(name) : name;
        var clean = GroupRe.Replace(baseName, string.Empty);

        int? season = null;
        var episodes = new List<int>();
        int cut = -1;

        var m = EpisodeRe.Match(clean);
        if (m.Success)
        {
            season = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
            AddEpisodes(episodes, m);
            cut = m.Index;
        }
        else if (SeasonRe.Match(clean) is { Success: true } sm)
        {
            season = int.Parse(sm.Groups["s"].Value, CultureInfo.InvariantCulture);
            cut = sm.Index;
        }
        else if (AbsoluteRe.Match(clean) is { Success: true } am)
        {
            episodes.Add(int.Parse(am.Groups["e"].Value, CultureInfo.InvariantCulture));
            cut = am.Index;
        }

        // Épisodes contenus dans un dossier (pack de saison).
        foreach (var f in videoFiles ?? Enumerable.Empty<string>())
        {
            var fn = GroupRe.Replace(Path.GetFileNameWithoutExtension(f), string.Empty);
            if (EpisodeRe.Match(fn) is { Success: true } fm)
            {
                season ??= int.Parse(fm.Groups["s"].Value, CultureInfo.InvariantCulture);
                AddEpisodes(episodes, fm);
            }
            else if (AbsoluteRe.Match(fn) is { Success: true } fa)
            {
                episodes.Add(int.Parse(fa.Groups["e"].Value, CultureInfo.InvariantCulture));
            }
        }

        if (cut < 0 && season is null && episodes.Count == 0)
        {
            return null;
        }

        var title = cut > 0 ? clean[..cut] : clean;
        title = JunkRe.Replace(title, string.Empty);
        title = Regex.Replace(title, @"[._]+", " ");
        title = YearRe.Replace(title, string.Empty);
        title = Regex.Replace(title, @"[\s\-–(\[]+$", string.Empty);
        title = Regex.Replace(title, @"\s{2,}", " ").Trim();
        if (title.Length == 0)
        {
            return null;
        }

        return new Parsed(title, season, episodes.Distinct().OrderBy(e => e).ToList());
    }

    private static void AddEpisodes(List<int> list, Match m)
    {
        var e = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture);
        list.Add(e);
        if (m.Groups["e2"].Success && int.TryParse(m.Groups["e2"].Value, out var e2) && e2 > e && e2 - e < 30)
        {
            for (var i = e + 1; i <= e2; i++)
            {
                list.Add(i);
            }
        }
    }

    /// <summary>Analyse un élément du dossier surveillé pour une destination (SERIES ou ANIME).</summary>
    public static SeriesPlacement? Analyze(string itemPath, string destDir)
    {
        var name = Path.GetFileName(itemPath);
        var videos = Directory.Exists(itemPath)
            ? FileOps.IterFiles(itemPath).Where(f => FileOps.VideoExt.Contains(f.Extension)).Select(f => f.Name).ToList()
            : new List<string>();
        var parsed = Parse(name, videos);
        if (parsed is null)
        {
            return null;
        }

        var shows = Directory.Exists(destDir)
            ? new DirectoryInfo(destDir).EnumerateDirectories().Where(d => !d.Name.StartsWith('.')).Select(d => d.Name).ToList()
            : new List<string>();
        var show = BestMatch(parsed.Title, shows);

        var placement = new SeriesPlacement
        {
            Show = show ?? SafeFolderName(ToTitle(parsed.Title)),
            ShowExists = show is not null,
            Season = parsed.Season,
            Episodes = parsed.Episodes,
        };

        var showDir = Path.Combine(destDir, placement.Show);
        var seasonDirs = placement.ShowExists
            ? new DirectoryInfo(showDir).EnumerateDirectories()
                .Select(d => (d.Name, M: SeasonFolderRe.Match(d.Name)))
                .Where(x => x.M.Success)
                .Select(x => (x.Name, Num: int.Parse(x.M.Groups["num"].Value, CultureInfo.InvariantCulture), x.M))
                .ToList()
            : new List<(string Name, int Num, Match M)>();

        if (parsed.Season is { } season)
        {
            var existing = seasonDirs.FirstOrDefault(s => s.Num == season);
            if (existing.Name is not null)
            {
                placement.SeasonFolder = existing.Name;
                placement.SeasonExists = true;
            }
            else
            {
                placement.SeasonFolder = NewSeasonFolderName(season, seasonDirs.Select(s => s.M).FirstOrDefault());
            }
        }
        else if (seasonDirs.Count > 0)
        {
            // Anime en numérotation absolue : dernière saison existante.
            var last = seasonDirs.OrderBy(s => s.Num).Last();
            placement.Season = last.Num;
            placement.SeasonFolder = last.Name;
            placement.SeasonExists = true;
        }

        var targetDir = Path.Combine(destDir, placement.RelativeTarget);
        if (Directory.Exists(targetDir))
        {
            var existingVideos = Directory.EnumerateFiles(targetDir)
                .Where(f => FileOps.VideoExt.Contains(Path.GetExtension(f))).ToList();
            placement.ExistingVideos = existingVideos.Count;
            var present = new HashSet<int>();
            foreach (var f in existingVideos)
            {
                var p = Parse(Path.GetFileName(f));
                if (p is not null)
                {
                    present.UnionWith(p.Episodes);
                }
            }

            placement.AlreadyThere = placement.Episodes.Where(present.Contains).ToList();
        }

        return placement;
    }

    /// <summary>Nom d'une nouvelle saison, sur le modèle des saisons voisines (« S03 », « Season.03 », « Saison 3 »).</summary>
    private static string NewSeasonFolderName(int season, Match? model)
    {
        if (model is null)
        {
            return $"Season {season:00}";
        }

        var digits = model.Groups["num"].Value.Length;
        return model.Groups["prefix"].Value + model.Groups["sep"].Value + season.ToString(new string('0', Math.Max(1, digits)), CultureInfo.InvariantCulture);
    }

    /// <summary>Dossier de série qui correspond le mieux au titre (null = aucun).</summary>
    public static string? BestMatch(string title, IEnumerable<string> folders)
    {
        var t = Tokens(title);
        if (t.Count == 0)
        {
            return null;
        }

        string? best = null;
        double bestScore = 0;
        foreach (var f in folders)
        {
            var ft = Tokens(f);
            if (ft.Count == 0)
            {
                continue;
            }

            double score;
            if (t.SetEquals(ft))
            {
                score = 1;
            }
            else
            {
                var common = t.Intersect(ft).Count();
                var small = Math.Min(t.Count, ft.Count);
                var large = Math.Max(t.Count, ft.Count);

                // Un nom entièrement contenu dans l'autre (« The Office » ↔ « The Office US ») ;
                // un seul mot en commun ne suffit pas.
                score = common == small && common >= 2 ? 0.7 + (0.2 * common / large) : (double)common / (t.Count + ft.Count - common);
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = f;
            }
        }

        return bestScore >= 0.7 ? best : null;
    }

    private static HashSet<string> Tokens(string s)
    {
        s = YearRe.Replace(s, " ");
        var norm = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '’')
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    }

    private static string ToTitle(string s) =>
        s.Any(char.IsLower) ? s : CultureInfo.GetCultureInfo("fr-FR").TextInfo.ToTitleCase(s.ToLowerInvariant());

    private static string SafeFolderName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }))
        {
            s = s.Replace(c, ' ');
        }

        return s.Trim().TrimStart('.');
    }
}
