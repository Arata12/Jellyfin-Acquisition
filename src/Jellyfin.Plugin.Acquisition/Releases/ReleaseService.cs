using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr;
using Jellyfin.Plugin.Acquisition.Adapters.Prowlarr.Models;
using Jellyfin.Plugin.Acquisition.Adapters.Radarr;
using Jellyfin.Plugin.Acquisition.Adapters.Sonarr;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Releases;

public class ReleaseService : IReleaseService
{
    private static readonly Regex YearRegex = new(@"\b(19\d{2}|20\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex TrailingParenthesesYear = new(@"\s*\((?:19\d{2}|20\d{2})\)\s*$", RegexOptions.Compiled);
    private static readonly Regex TrailingYearPattern = new(@"\b(19\d{2}|20\d{2})$", RegexOptions.Compiled);
    private static readonly Regex SeasonOrEpisodePattern = new(@"[.\s_\-\(\[]+(?:s\d{1,2}(?:[.\s_\-]*e\d{1,3})?|season\s*\d+|temporada\s*\d+|ep?\d{1,3}|episode\s*\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LeadingGroupTag = new(@"^\s*(?:\[[^\]]*\]\s*)+", RegexOptions.Compiled);
    private static readonly Regex AbsoluteEpisodeSeparator = new(@"\s-\s\d{1,4}(?:v\d)?(?=[\s\[\(.]|$)", RegexOptions.Compiled);
    private static readonly Regex QualityTokenPattern = new(@"[.\s_\-\(\[]+(?:2160p|1080p|1080i|720p|480p|4k|uhd|bluray|blu-ray|web-dl|webdl|webrip|web-rip|hdtv|dvdrip|x264|x265|hevc|avc|remux)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Evaluated candidates kept per search; the list is persisted with the request.</summary>
    public const int MaxCandidates = 100;

    /// <summary>Concurrent Arr /parse calls per search, to keep the search fast without flooding Arr.</summary>
    private const int ParseConcurrency = 4;

    /// <summary>Extra title searches with other names of the title when nothing eligible was found.</summary>
    private const int MaxAliasSearches = 2;

    private readonly IProwlarrClient _prowlarrClient;
    private readonly ISonarrClient _sonarrClient;
    private readonly IRadarrClient _radarrClient;
    private readonly ILogger<ReleaseService> _logger;
    private readonly ITitleAliasService? _aliasService;

    public ReleaseService(
        IProwlarrClient prowlarrClient,
        ISonarrClient sonarrClient,
        IRadarrClient radarrClient,
        ILogger<ReleaseService> logger,
        ITitleAliasService? aliasService = null)
    {
        _prowlarrClient = prowlarrClient;
        _sonarrClient = sonarrClient;
        _radarrClient = radarrClient;
        _logger = logger;
        _aliasService = aliasService;
    }

    public Task<List<ReleaseCandidate>> SearchAndEvaluateReleasesAsync(AcquisitionRequest request, CancellationToken ct = default)
    {
        var activeUnit = request.Units.FirstOrDefault(u => u.Status != RequestStatus.Available);
        return SearchAndEvaluateReleasesForUnitAsync(request, activeUnit, ct);
    }

    public async Task<List<ReleaseCandidate>> SearchAndEvaluateReleasesForUnitAsync(AcquisitionRequest request, DownloadUnit? unit, CancellationToken ct = default)
    {
        var config = Plugin.Instance?.Configuration;
        var pQuery = new ProwlarrSearchQuery
        {
            Query = request.Title,
            Kind = request.Kind,
            IsAnime = string.Equals(request.SeriesType, "anime", StringComparison.OrdinalIgnoreCase),
            TvdbId = request.ProviderIds.Tvdb,
            TmdbId = request.ProviderIds.Tmdb,
            ImdbId = request.ProviderIds.Imdb
        };

        if (unit != null)
        {
            pQuery.Season = unit.SeasonNumber;
            if (unit.EpisodeNumbers.Count == 1)
            {
                pQuery.Episode = unit.EpisodeNumbers[0];
                if (pQuery.IsAnime && unit.AbsoluteEpisodeNumbers.Count == 1)
                {
                    pQuery.AbsoluteEpisode = unit.AbsoluteEpisodeNumbers[0];
                }
            }
        }
        else if (request.Kind == MediaKind.Series)
        {
            if (request.Scope.TargetEpisodes.Count == 1)
            {
                pQuery.Season = request.Scope.TargetEpisodes[0].SeasonNumber;
                pQuery.Episode = request.Scope.TargetEpisodes[0].EpisodeNumber;
            }
            else if (request.Scope.SeasonNumbers.Count == 1)
            {
                pQuery.Season = request.Scope.SeasonNumbers[0];
                if (request.Scope.EpisodeNumbers.Count == 1)
                {
                    pQuery.Episode = request.Scope.EpisodeNumbers[0];
                }
            }
        }

        // The ID is what identifies the title (user decision): the search by ID runs next to the title search and
        // finds releases named in another language; the other names of the title are read by ID as well
        var aliasTask = GetAliasesAsync(request, ct);
        var byIdTask = SearchByIdAsync(pQuery, ct);
        var byTitle = await _prowlarrClient.SearchAsync(pQuery, ct).ConfigureAwait(false);
        var byId = await byIdTask.ConfigureAwait(false);
        var aliases = await aliasTask.ConfigureAwait(false);

        var seen = new Dictionary<string, ReleaseCandidate>(StringComparer.OrdinalIgnoreCase);
        var candidates = AddCandidates(byId, fromIdSearch: true, request, seen);
        candidates.AddRange(AddCandidates(byTitle, fromIdSearch: false, request, seen));
        await EvaluateAllAsync(candidates, request, unit, aliases, config, ct).ConfigureAwait(false);

        // Nothing usable under the requested name: search again with the other names of the title (romaji, scene names)
        if (!candidates.Any(c => c.IsEligible))
        {
            foreach (var alias in SearchableAliases(request.Title, aliases).Take(MaxAliasSearches))
            {
                var aliasQuery = CloneQuery(pQuery, alias);
                List<ProwlarrRelease> byAlias;
                try
                {
                    byAlias = await _prowlarrClient.SearchAsync(aliasQuery, ct).ConfigureAwait(false);
                }
                catch (ProwlarrUnavailableException ex)
                {
                    _logger.LogDebug(ex, "Search by alternative title {Alias} failed", alias);
                    continue;
                }

                var added = AddCandidates(byAlias, fromIdSearch: false, request, seen);
                await EvaluateAllAsync(added, request, unit, aliases, config, ct).ConfigureAwait(false);
                candidates.AddRange(added);
            }
        }

        // Deterministic ordering (user decision): eligible first; a whole-season pack when a whole season was asked for;
        // a torrent with seeders before one without (it would never download); then the most quality points, then the
        // most seeders among equal points, then title. A release of another title never sits above one of the requested
        // title, whatever its seeders.
        var wantsWholeSeason = request.Kind == MediaKind.Series && request.Scope.SeasonNumbers.Count > 0 && request.Scope.EpisodeNumbers.Count == 0;
        return candidates
            .OrderByDescending(c => c.IsEligible)
            .ThenByDescending(c => c.IdentityVerified && !c.IdentityRejected)
            .ThenByDescending(c => wantsWholeSeason && c.IsSeasonPack)
            .ThenByDescending(c => c.Seeders > 0)
            .ThenByDescending(c => c.Score)
            .ThenByDescending(c => c.Seeders)
            .ThenBy(c => c.Title, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> GetAliasesAsync(AcquisitionRequest request, CancellationToken ct)
    {
        if (_aliasService == null) return [];

        try
        {
            return await _aliasService.GetAliasesAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not read the alternative titles of {Title}", request.Title);
            return [];
        }
    }

    /// <summary>Search by the requested IDs; a failure here never hides the results of the title search.</summary>
    private async Task<List<ProwlarrRelease>> SearchByIdAsync(ProwlarrSearchQuery query, CancellationToken ct)
    {
        var idQuery = CloneQuery(query, query.Query);
        idQuery.ById = true;
        if (ProwlarrClient.BuildIdQuery(idQuery).Length == 0) return [];

        try
        {
            return await _prowlarrClient.SearchAsync(idQuery, ct).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Search by ID failed for {Title}", query.Query);
            return [];
        }
    }

    private static ProwlarrSearchQuery CloneQuery(ProwlarrSearchQuery query, string title) => new()
    {
        Query = title,
        Kind = query.Kind,
        Season = query.Season,
        Episode = query.Episode,
        AbsoluteEpisode = query.AbsoluteEpisode,
        IsAnime = query.IsAnime,
        TvdbId = query.TvdbId,
        TmdbId = query.TmdbId,
        ImdbId = query.ImdbId,
        IndexerIds = query.IndexerIds
    };

    /// <summary>Other names worth a title search: Latin script (what indexers are searched with) and not the same name.</summary>
    private static IEnumerable<string> SearchableAliases(string title, IReadOnlyList<string> aliases)
    {
        var used = new HashSet<string> { NormalizeTitle(title) };
        foreach (var alias in aliases)
        {
            var norm = NormalizeTitle(alias);
            if (norm.Length < 3 || !norm.Any(char.IsLetter) || norm.Any(c => char.IsLetter(c) && c > '\u024F')) continue;
            if (used.Add(norm)) yield return alias;
        }
    }

    private async Task EvaluateAllAsync(List<ReleaseCandidate> candidates, AcquisitionRequest request, DownloadUnit? unit, IReadOnlyList<string> aliases, Configuration.PluginConfiguration? config, CancellationToken ct)
    {
        // Enrich with Arr /parse with bounded concurrency (sequential calls made large result lists very slow)
        using (var throttle = new SemaphoreSlim(ParseConcurrency))
        {
            await Task.WhenAll(candidates.Select(async candidate =>
            {
                await throttle.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await TryEnrichFromArrParseAsync(candidate, request, ct).ConfigureAwait(false);
                }
                finally
                {
                    throttle.Release();
                }
            })).ConfigureAwait(false);
        }

        foreach (var candidate in candidates)
        {
            EvaluateCandidate(candidate, request, unit, aliases, config);
        }
    }

    /// <summary>New candidates for the releases not seen yet; a release seen again in the search by ID is marked as such.</summary>
    private static List<ReleaseCandidate> AddCandidates(IEnumerable<ProwlarrRelease> releases, bool fromIdSearch, AcquisitionRequest request, Dictionary<string, ReleaseCandidate> seen)
    {
        var candidates = new List<ReleaseCandidate>();
        foreach (var pr in releases)
        {
            var key = !string.IsNullOrEmpty(pr.InfoHash) ? pr.InfoHash : $"{pr.IndexerId}:{pr.Guid ?? pr.Title}";
            if (seen.TryGetValue(key, out var existing))
            {
                existing.FromIdSearch |= fromIdSearch;
                continue;
            }

            var candidate = new ReleaseCandidate
            {
                Title = pr.Title,
                InfoHash = pr.InfoHash,
                MagnetUri = pr.MagnetUrl,
                DownloadUrl = pr.DownloadUrl,
                Size = pr.Size,
                Seeders = pr.Seeders ?? 0,
                Leechers = pr.Leechers ?? 0,
                Indexer = pr.Indexer ?? "Unknown",
                IndexerPriority = 1,
                IsTorrent = string.Equals(pr.Protocol, "torrent", StringComparison.OrdinalIgnoreCase) ||
                            !string.IsNullOrEmpty(pr.MagnetUrl) ||
                            (pr.DownloadUrl != null && pr.DownloadUrl.Contains(".torrent", StringComparison.OrdinalIgnoreCase))
            };

            // Parse season/episode and tokens
            var (sNum, epNum, isPack) = ReleaseParser.ParseSeasonEpisode(candidate.Title);
            candidate.SeasonNumber = sNum;
            candidate.EpisodeNumber = epNum;
            candidate.IsSeasonPack = isPack;
            if (request.Kind == MediaKind.Series && sNum < 0)
            {
                candidate.AbsoluteEpisodeNumber = ReleaseParser.ParseAbsoluteEpisode(candidate.Title);
            }

            candidate.Codec = ReleaseParser.NormalizeCodec(candidate.Title);
            candidate.Resolution = ReleaseParser.NormalizeResolution(candidate.Title);
            candidate.Source = ReleaseParser.NormalizeSource(candidate.Title);

            // IDs the indexer tagged the release with decide first, over Arr, the name and the seeders
            var (idsMatch, idReason) = CompareReleaseIds(pr, request);
            if (idsMatch == true)
            {
                candidate.IdentityVerified = true;
            }
            else if (idsMatch == false)
            {
                candidate.IdentityRejected = true;
                candidate.RejectionReasons.Add(idReason!);
            }

            candidate.FromIdSearch = fromIdSearch;
            seen[key] = candidate;
            candidates.Add(candidate);
        }

        return candidates;
    }

    /// <summary>
    /// True when the release carries one of the requested IDs and none that differs, false when any ID it carries
    /// differs (conservative: an indexer tagging a release with another title's ID is never trusted), null without IDs.
    /// </summary>
    private static (bool? Match, string? Reason) CompareReleaseIds(ProwlarrRelease release, AcquisitionRequest request)
    {
        var ids = request.ProviderIds ?? new ProviderIds();
        var comparisons = new List<(string Name, int Found, int Expected)>();
        if (request.Kind == MediaKind.Series && release.TvdbId > 0 && int.TryParse(ids.Tvdb, out var tvdb) && tvdb > 0)
        {
            comparisons.Add(("TVDB", release.TvdbId, tvdb));
        }

        if (release.TmdbId > 0 && int.TryParse(ids.Tmdb, out var tmdb) && tmdb > 0)
        {
            comparisons.Add(("TMDB", release.TmdbId, tmdb));
        }

        if (release.ImdbId > 0 && ids.Imdb != null && ids.Imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(ids.Imdb.AsSpan(2), out var imdb) && imdb > 0)
        {
            comparisons.Add(("IMDb", release.ImdbId, imdb));
        }

        if (comparisons.Count == 0) return (null, null);

        var wrong = comparisons.FirstOrDefault(c => c.Found != c.Expected);
        return wrong.Name == null
            ? (true, null)
            : (false, $"Es otro título: el indexador la etiqueta como {wrong.Name} {wrong.Found} y se buscaba {wrong.Expected}.");
    }

    private async Task TryEnrichFromArrParseAsync(ReleaseCandidate candidate, AcquisitionRequest request, CancellationToken ct)
    {
        try
        {
            if (request.Kind == MediaKind.Series)
            {
                var parseResult = await _sonarrClient.ParseAsync(candidate.Title, ct).ConfigureAwait(false);
                if (parseResult != null)
                {
                    // Check identity via parsed series (R05/N05), unless the release's own IDs already decided it
                    bool? seriesMatches = null;
                    string? mismatch = null;
                    if (parseResult.Series != null)
                    {
                        if (request.ArrId.HasValue && parseResult.Series.Id > 0)
                        {
                            seriesMatches = parseResult.Series.Id == request.ArrId.Value;
                            mismatch = $"Es otra serie: Sonarr la identifica con otro ID (serie {parseResult.Series.Id}; se buscaba la {request.ArrId.Value}).";
                        }
                        else if (!string.IsNullOrEmpty(request.ProviderIds.Tvdb) && parseResult.Series.TvdbId > 0)
                        {
                            seriesMatches = parseResult.Series.TvdbId.ToString() == request.ProviderIds.Tvdb;
                            mismatch = $"Es otra serie: Sonarr la identifica como TVDB {parseResult.Series.TvdbId} y se buscaba TVDB {request.ProviderIds.Tvdb}.";
                        }
                        else if (!string.IsNullOrEmpty(parseResult.Series.Title))
                        {
                            seriesMatches = string.Equals(parseResult.Series.Title.Trim(), request.Title.Trim(), StringComparison.OrdinalIgnoreCase);
                            mismatch = $"Es otra serie: Sonarr la identifica como «{parseResult.Series.Title}» y se buscaba «{request.Title}».";
                        }
                    }

                    ApplyArrIdentity(candidate, seriesMatches, mismatch);

                    if (parseResult.ParsedEpisodeInfo != null)
                    {
                        var info = parseResult.ParsedEpisodeInfo;
                        if (info.SeasonNumber > 0) candidate.SeasonNumber = info.SeasonNumber;
                        if (info.EpisodeNumbers.Count > 0) candidate.EpisodeNumber = info.EpisodeNumbers[0];
                        if (info.AbsoluteEpisodeNumbers.Count > 0) candidate.AbsoluteEpisodeNumber = info.AbsoluteEpisodeNumbers[0];
                        if (info.Quality?.Quality != null)
                        {
                            var q = info.Quality.Quality;
                            if (q.Resolution > 0 && candidate.Resolution == "Unknown")
                                candidate.Resolution = $"{q.Resolution}p";
                            // Arr's parser recognises far more naming styles and is what will import the file
                            var arrSource = ReleaseParser.NormalizeArrSource(q.Source);
                            if (arrSource != "Unknown")
                                candidate.Source = arrSource;
                        }
                        if (info.Languages.Count > 0)
                        {
                            candidate.Languages = info.Languages.Where(l => l.Name != null).Select(l => l.Name!).ToList();
                        }
                    }

                    // When Sonarr matched the series, it maps absolute (anime) numbering to real season/episode
                    var mapped = parseResult.Episodes.FirstOrDefault();
                    if (seriesMatches == true && candidate.IdentityVerified && mapped != null && mapped.SeasonNumber > 0 && candidate.EpisodeNumber <= 0)
                    {
                        candidate.SeasonNumber = mapped.SeasonNumber;
                        candidate.EpisodeNumber = mapped.EpisodeNumber;
                    }
                }
            }
            else if (request.Kind == MediaKind.Movie)
            {
                var parseResult = await _radarrClient.ParseAsync(candidate.Title, ct).ConfigureAwait(false);
                if (parseResult != null)
                {
                    // Check identity via parsed movie (R05/N05), unless the release's own IDs already decided it
                    bool? movieMatches = null;
                    string? mismatch = null;
                    if (parseResult.Movie != null)
                    {
                        if (request.ArrId.HasValue && parseResult.Movie.Id > 0)
                        {
                            movieMatches = parseResult.Movie.Id == request.ArrId.Value;
                            mismatch = $"Es otra película: Radarr la identifica con otro ID (película {parseResult.Movie.Id}; se buscaba la {request.ArrId.Value}).";
                        }
                        else if (!string.IsNullOrEmpty(request.ProviderIds.Tmdb) && parseResult.Movie.TmdbId > 0)
                        {
                            movieMatches = parseResult.Movie.TmdbId.ToString() == request.ProviderIds.Tmdb;
                            mismatch = $"Es otra película: Radarr la identifica como TMDB {parseResult.Movie.TmdbId} y se buscaba TMDB {request.ProviderIds.Tmdb}.";
                        }
                        else if (!string.IsNullOrEmpty(parseResult.Movie.Title))
                        {
                            movieMatches = string.Equals(parseResult.Movie.Title.Trim(), request.Title.Trim(), StringComparison.OrdinalIgnoreCase);
                            mismatch = $"Es otra película: Radarr la identifica como «{parseResult.Movie.Title}» y se buscaba «{request.Title}».";
                        }
                    }

                    ApplyArrIdentity(candidate, movieMatches, mismatch);

                    if (parseResult.ParsedMovieInfo != null)
                    {
                        var info = parseResult.ParsedMovieInfo;
                        if (info.Quality?.Quality != null)
                        {
                            var q = info.Quality.Quality;
                            if (q.Resolution > 0 && candidate.Resolution == "Unknown")
                                candidate.Resolution = $"{q.Resolution}p";
                            // Arr's parser recognises far more naming styles and is what will import the file
                            var arrSource = ReleaseParser.NormalizeArrSource(q.Source);
                            if (arrSource != "Unknown")
                                candidate.Source = arrSource;
                        }
                        if (info.Languages.Count > 0)
                        {
                            candidate.Languages = info.Languages.Where(l => l.Name != null).Select(l => l.Name!).ToList();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse title {Title} via Arr", candidate.Title);
        }
    }

    /// <summary>Arr's verdict, when it recognised the release and the release's own IDs did not already decide.</summary>
    private static void ApplyArrIdentity(ReleaseCandidate candidate, bool? matches, string? mismatch)
    {
        if (matches == null || candidate.IdentityVerified || candidate.IdentityRejected) return;

        if (matches.Value)
        {
            candidate.IdentityVerified = true;
        }
        else
        {
            candidate.IdentityRejected = true;
            candidate.RejectionReasons.Add(mismatch!);
        }
    }

    private const string NotTorrentReason = "No es un torrent.";

    private static void EvaluateCandidate(ReleaseCandidate candidate, AcquisitionRequest request, DownloadUnit? unit, IReadOnlyList<string> aliases, Configuration.PluginConfiguration? config)
    {
        var reasons = new List<string>(candidate.RejectionReasons);

        // 1. Mandatory: Protocol must be torrent
        if (!candidate.IsTorrent)
        {
            reasons.Add(NotTorrentReason);
        }

        // 2. Mandatory: Identity verification & fallback (N05, H07 & J03). A release identified as another title (by
        // ID or by Arr) stays rejected; one the indexer returned for the requested IDs is that title, whatever its name
        if (!candidate.IdentityVerified && !candidate.IdentityRejected && candidate.FromIdSearch)
        {
            candidate.IdentityVerified = true;
        }

        var reasonsBeforeIdentity = reasons.Count;
        if (!candidate.IdentityVerified && !candidate.IdentityRejected)
        {
            // Clean trailing year in parentheses like "Doctor Who (2005)" without affecting numeric titles like "1923"
            var normReq = NormalizeTitle(TrailingParenthesesYear.Replace(request.Title, string.Empty).Trim());

            // Anime releases start with "[Group]"; it is not part of the title
            var strippedTitle = LeadingGroupTag.Replace(candidate.Title, string.Empty);
            var prefix = ExtractTitlePrefix(strippedTitle);
            var normCand = NormalizeTitle(prefix);

            var titlesMatch = false;
            int? candidateYear = null;

            // The requested title first, then its other names read by ID (another language, romaji, scene names)
            foreach (var name in aliases.Prepend(request.Title))
            {
                var norm = NormalizeTitle(TrailingParenthesesYear.Replace(name, string.Empty).Trim());
                if (norm.Length == 0) continue;

                if (norm == normCand || StripLeadingArticle(norm) == StripLeadingArticle(normCand))
                {
                    // Exact title match. Check if candidate title has a year token after the title prefix
                    titlesMatch = true;
                    var yearMatch = YearRegex.Match(strippedTitle[prefix.Length..]);
                    if (yearMatch.Success)
                    {
                        candidateYear = int.Parse(yearMatch.Value);
                    }

                    break;
                }

                // Check if candidate prefix ends with a 4-digit disambiguation year: "<Title> <Year>"
                var trailingYearMatch = TrailingYearPattern.Match(normCand);
                if (trailingYearMatch.Success)
                {
                    var titleWithoutYear = normCand[..trailingYearMatch.Index].Trim();
                    if (titleWithoutYear == norm || StripLeadingArticle(titleWithoutYear) == StripLeadingArticle(norm))
                    {
                        titlesMatch = true;
                        candidateYear = int.Parse(trailingYearMatch.Groups[1].Value);
                        break;
                    }
                }
            }

            if (!titlesMatch)
            {
                reasons.Add($"El título no coincide: la release se llama «{normCand}» y se buscaba «{normReq}».");
            }
            else
            {
                if (request.Kind == MediaKind.Movie && request.Year.HasValue)
                {
                    if (!candidateYear.HasValue)
                    {
                        var allYears = YearRegex.Matches(candidate.Title).Select(m => int.Parse(m.Value)).ToList();
                        if (allYears.Count == 0)
                        {
                            reasons.Add("No indica el año y no se puede comprobar que sea la película pedida.");
                        }
                        else if (!allYears.Any(y => Math.Abs(y - request.Year.Value) <= 1))
                        {
                            reasons.Add($"Es de otro año ({string.Join(", ", allYears)}; se buscaba {request.Year.Value}).");
                        }
                        else
                        {
                            candidate.IdentityVerified = true;
                        }
                    }
                    else if (Math.Abs(candidateYear.Value - request.Year.Value) > 1)
                    {
                        reasons.Add($"Es de otro año ({candidateYear.Value}; se buscaba {request.Year.Value}).");
                    }
                    else
                    {
                        candidate.IdentityVerified = true;
                    }
                }
                else if (request.Kind == MediaKind.Series)
                {
                    if (request.Year.HasValue && candidateYear.HasValue)
                    {
                        if (Math.Abs(candidateYear.Value - request.Year.Value) > 1)
                        {
                            reasons.Add($"Es de otro año ({candidateYear.Value}; se buscaba {request.Year.Value}).");
                        }
                        else
                        {
                            candidate.IdentityVerified = true;
                        }
                    }
                    else
                    {
                        candidate.IdentityVerified = true;
                    }
                }
                else
                {
                    candidate.IdentityVerified = true;
                }
            }

            if (!candidate.IdentityVerified && reasons.Count == reasonsBeforeIdentity)
            {
                reasons.Add("No se pudo comprobar que sea el título pedido.");
            }
        }

        // 3. Mandatory: Scope compatibility (N01)
        if (request.Kind == MediaKind.Series)
        {
            int? targetSeason = null;
            int? targetEpisode = null;
            bool isFullSeason = false;

            if (unit != null)
            {
                targetSeason = unit.SeasonNumber;
                isFullSeason = unit.IsFullSeasonPack;
                if (unit.EpisodeNumbers.Count == 1)
                {
                    targetEpisode = unit.EpisodeNumbers[0];
                }
            }
            else if (request.Scope.TargetEpisodes.Count > 0)
            {
                if (request.Scope.TargetEpisodes.Count == 1)
                {
                    targetSeason = request.Scope.TargetEpisodes[0].SeasonNumber;
                    targetEpisode = request.Scope.TargetEpisodes[0].EpisodeNumber;
                }
                else
                {
                    var distinctSeasons = request.Scope.TargetEpisodes.Select(t => t.SeasonNumber).Distinct().ToList();
                    if (distinctSeasons.Count == 1)
                    {
                        targetSeason = distinctSeasons[0];
                    }
                }
            }
            else if (request.Scope.SeasonNumbers.Count > 0)
            {
                targetSeason = request.Scope.SeasonNumbers[0];
                if (request.Scope.EpisodeNumbers.Count == 1)
                {
                    targetEpisode = request.Scope.EpisodeNumbers[0];
                }
                else if (request.Scope.EpisodeNumbers.Count == 0)
                {
                    isFullSeason = true;
                }
            }

            if (targetSeason.HasValue)
            {
                if (candidate.SeasonNumber > 0 && candidate.SeasonNumber != targetSeason.Value)
                {
                    reasons.Add($"Es otra temporada (S{candidate.SeasonNumber:D2}; se buscaba S{targetSeason.Value:D2}).");
                }

                if (isFullSeason)
                {
                    if (!candidate.IsSeasonPack)
                    {
                        reasons.Add("Es un episodio suelto y se buscaba la temporada completa.");
                    }
                }
                else if (targetEpisode.HasValue)
                {
                    if (candidate.IsSeasonPack)
                    {
                        reasons.Add("Es un pack de temporada y se buscaba un solo episodio.");
                    }
                    else if (candidate.EpisodeNumber > 0 && candidate.EpisodeNumber != targetEpisode.Value)
                    {
                        reasons.Add($"Es otro episodio (E{candidate.EpisodeNumber:D2}; se buscaba E{targetEpisode.Value:D2}).");
                    }
                    else if (candidate.EpisodeNumber <= 0 && candidate.AbsoluteEpisodeNumber > 0)
                    {
                        // Absolute (anime) numbering: compare against the absolute number Sonarr assigned to the target
                        var targetAbsolute = unit != null && unit.AbsoluteEpisodeNumbers.Count == 1 ? unit.AbsoluteEpisodeNumbers[0] : (int?)null;
                        if (targetAbsolute == null)
                        {
                            reasons.Add("Usa numeración absoluta y Sonarr no tiene ese número para el episodio pedido.");
                        }
                        else if (candidate.AbsoluteEpisodeNumber != targetAbsolute.Value)
                        {
                            reasons.Add($"Es otro episodio (número absoluto {candidate.AbsoluteEpisodeNumber}; se buscaba el {targetAbsolute.Value}).");
                        }
                    }
                    else if (candidate.EpisodeNumber <= 0 && !candidate.IsSeasonPack)
                    {
                        reasons.Add("No indica qué episodio es y se buscaba uno concreto.");
                    }
                }
            }
        }

        // Everything but the protocol is an identity or scope problem a manual choice must not override
        candidate.IdentityOrScopeMismatch = reasons.Any(r => r != NotTorrentReason);

        candidate.IsEligible = reasons.Count == 0;
        candidate.RejectionReasons = reasons.Distinct().ToList();

        // 5. Quality points (user decision): each resolution, source and codec is worth the points set in the
        // configuration and the release with the highest total wins. Nothing is rejected for its quality.
        candidate.Score = QualityPoints(candidate, config ?? new Configuration.PluginConfiguration());
    }

    /// <summary>Points of a release: those of its resolution, plus its source, plus its codec.</summary>
    public static int QualityPoints(ReleaseCandidate candidate, Configuration.PluginConfiguration config)
    {
        var resolution = candidate.Resolution switch
        {
            "2160p" => config.Points2160p,
            "1080p" => config.Points1080p,
            "720p" => config.Points720p,
            "480p" => config.Points480p,
            _ => config.PointsResolutionUnknown
        };
        var source = candidate.Source switch
        {
            "WEB-DL" => config.PointsWebDl,
            "Bluray" => config.PointsBluray,
            "WEBRip" => config.PointsWebRip,
            "HDTV" => config.PointsHdtv,
            _ => config.PointsSourceUnknown
        };
        var codec = candidate.Codec switch
        {
            "H.264" => config.PointsH264,
            "H.265" => config.PointsH265,
            "AV1" => config.PointsAv1,
            _ => config.PointsCodecUnknown
        };
        return resolution + source + codec;
    }

    private static string ExtractTitlePrefix(string candidateTitle)
    {
        int cutoff = candidateTitle.Length;

        var sMatch = SeasonOrEpisodePattern.Match(candidateTitle);
        if (sMatch.Success && sMatch.Index < cutoff) cutoff = sMatch.Index;

        var absMatch = AbsoluteEpisodeSeparator.Match(candidateTitle);
        if (absMatch.Success && absMatch.Index < cutoff) cutoff = absMatch.Index;

        var qMatch = QualityTokenPattern.Match(candidateTitle);
        if (qMatch.Success && qMatch.Index < cutoff) cutoff = qMatch.Index;

        return candidateTitle[..cutoff];
    }

    private static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        // Apostrophes vanish in release names ("Journeys"), other punctuation separates words ("Girlfriend, Girlfriend")
        var cleaned = Regex.Replace(title, @"['’`]", string.Empty);
        cleaned = Regex.Replace(cleaned, @"[._\-\(\)\[\]+:,!?;/~]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim().ToLowerInvariant();
        return cleaned;
    }

    private static string StripLeadingArticle(string title)
    {
        if (title.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) return title[4..].Trim();
        if (title.StartsWith("a ", StringComparison.OrdinalIgnoreCase)) return title[2..].Trim();
        if (title.StartsWith("an ", StringComparison.OrdinalIgnoreCase)) return title[3..].Trim();
        return title;
    }

    public ReleaseCandidate? PickBestRelease(IEnumerable<ReleaseCandidate> candidates)
    {
        return candidates.FirstOrDefault(c => c.IsEligible);
    }
}
