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

    private readonly IProwlarrClient _prowlarrClient;
    private readonly ISonarrClient _sonarrClient;
    private readonly IRadarrClient _radarrClient;
    private readonly ILogger<ReleaseService> _logger;

    public ReleaseService(
        IProwlarrClient prowlarrClient,
        ISonarrClient sonarrClient,
        IRadarrClient radarrClient,
        ILogger<ReleaseService> logger)
    {
        _prowlarrClient = prowlarrClient;
        _sonarrClient = sonarrClient;
        _radarrClient = radarrClient;
        _logger = logger;
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

        var prowlarrReleases = await _prowlarrClient.SearchAsync(pQuery, ct).ConfigureAwait(false);
        var candidates = new List<ReleaseCandidate>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pr in prowlarrReleases)
        {
            var key = !string.IsNullOrEmpty(pr.InfoHash) ? pr.InfoHash : $"{pr.IndexerId}:{pr.Guid ?? pr.Title}";
            if (!seenKeys.Add(key))
            {
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

            candidates.Add(candidate);
        }

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
            EvaluateCandidate(candidate, request, unit, config);
        }

        // Deterministic ordering (user decision): eligible first, then the release that best matches the quality
        // preferences (resolution > source > codec, in the score), then most seeders among equal quality, then title
        return candidates
            .OrderByDescending(c => c.IsEligible)
            .ThenByDescending(c => c.Score)
            .ThenByDescending(c => c.Seeders)
            .ThenBy(c => c.Title, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .ToList();
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
                    // Check identity via parsed series (R05/N05)
                    if (parseResult.Series != null)
                    {
                        if (request.ArrId.HasValue && parseResult.Series.Id > 0)
                        {
                            if (parseResult.Series.Id == request.ArrId.Value)
                            {
                                candidate.IdentityVerified = true;
                            }
                            else
                            {
                                candidate.RejectionReasons.Add($"Identity mismatch (Sonarr parsed series ID {parseResult.Series.Id} != expected {request.ArrId.Value})");
                            }
                        }
                        else if (!string.IsNullOrEmpty(request.ProviderIds.Tvdb) && parseResult.Series.TvdbId > 0)
                        {
                            if (parseResult.Series.TvdbId.ToString() == request.ProviderIds.Tvdb)
                            {
                                candidate.IdentityVerified = true;
                            }
                            else
                            {
                                candidate.RejectionReasons.Add($"Identity mismatch (Sonarr parsed TVDB {parseResult.Series.TvdbId} != expected {request.ProviderIds.Tvdb})");
                            }
                        }
                        else if (!string.IsNullOrEmpty(parseResult.Series.Title))
                        {
                            if (string.Equals(parseResult.Series.Title.Trim(), request.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                candidate.IdentityVerified = true;
                            }
                            else
                            {
                                candidate.RejectionReasons.Add($"Identity mismatch (Sonarr parsed title '{parseResult.Series.Title}' != expected '{request.Title}')");
                            }
                        }
                    }

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
                    if (candidate.IdentityVerified && mapped != null && mapped.SeasonNumber > 0 && candidate.EpisodeNumber <= 0)
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
                    // Check identity via parsed movie (R05/N05)
                    if (parseResult.Movie != null)
                    {
                        if (request.ArrId.HasValue && parseResult.Movie.Id > 0)
                        {
                            if (parseResult.Movie.Id == request.ArrId.Value)
                            {
                                candidate.IdentityVerified = true;
                            }
                            else
                            {
                                candidate.RejectionReasons.Add($"Identity mismatch (Radarr parsed movie ID {parseResult.Movie.Id} != expected {request.ArrId.Value})");
                            }
                        }
                        else if (!string.IsNullOrEmpty(request.ProviderIds.Tmdb) && parseResult.Movie.TmdbId > 0)
                        {
                            if (parseResult.Movie.TmdbId.ToString() == request.ProviderIds.Tmdb)
                            {
                                candidate.IdentityVerified = true;
                            }
                            else
                            {
                                candidate.RejectionReasons.Add($"Identity mismatch (Radarr parsed TMDb {parseResult.Movie.TmdbId} != expected {request.ProviderIds.Tmdb})");
                            }
                        }
                        else if (!string.IsNullOrEmpty(parseResult.Movie.Title))
                        {
                            if (string.Equals(parseResult.Movie.Title.Trim(), request.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                candidate.IdentityVerified = true;
                            }
                            else
                            {
                                candidate.RejectionReasons.Add($"Identity mismatch (Radarr parsed title '{parseResult.Movie.Title}' != expected '{request.Title}')");
                            }
                        }
                    }

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

    private static void EvaluateCandidate(ReleaseCandidate candidate, AcquisitionRequest request, DownloadUnit? unit, Configuration.PluginConfiguration? config)
    {
        var reasons = new List<string>(candidate.RejectionReasons);

        // 1. Mandatory: Protocol must be torrent
        if (!candidate.IsTorrent)
        {
            reasons.Add("Protocol is not torrent");
        }

        // 2. Mandatory: Identity verification & fallback (N05, H07 & J03)
        if (!candidate.IdentityVerified)
        {
            // Clean trailing year in parentheses like "Doctor Who (2005)" without affecting numeric titles like "1923"
            var cleanReq = TrailingParenthesesYear.Replace(request.Title, string.Empty).Trim();
            var normReq = NormalizeTitle(cleanReq);

            // Anime releases start with "[Group]"; it is not part of the title
            var strippedTitle = LeadingGroupTag.Replace(candidate.Title, string.Empty);
            var prefix = ExtractTitlePrefix(strippedTitle);
            var normCand = NormalizeTitle(prefix);

            var titlesMatch = normReq == normCand ||
                StripLeadingArticle(normReq) == StripLeadingArticle(normCand);

            int? candidateYear = null;

            if (!titlesMatch)
            {
                // Check if candidate prefix ends with a 4-digit disambiguation year: "<Title> <Year>"
                var trailingYearMatch = TrailingYearPattern.Match(normCand);
                if (trailingYearMatch.Success)
                {
                    var titleWithoutYear = normCand[..trailingYearMatch.Index].Trim();
                    if (titleWithoutYear == normReq || StripLeadingArticle(titleWithoutYear) == StripLeadingArticle(normReq))
                    {
                        titlesMatch = true;
                        candidateYear = int.Parse(trailingYearMatch.Groups[1].Value);
                    }
                }
            }
            else
            {
                // Exact title match. Check if candidate title has a year token after the title prefix
                var afterPrefix = strippedTitle[prefix.Length..];
                var yearMatch = YearRegex.Match(afterPrefix);
                if (yearMatch.Success)
                {
                    candidateYear = int.Parse(yearMatch.Value);
                }
            }

            if (!titlesMatch)
            {
                reasons.Add($"Title mismatch: release title '{normCand}' does not match requested title '{normReq}'");
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
                            reasons.Add("Year mismatch: movie release lacks required year token to verify identity");
                        }
                        else if (!allYears.Any(y => Math.Abs(y - request.Year.Value) <= 1))
                        {
                            reasons.Add($"Year mismatch (found {string.Join(',', allYears)}, expected {request.Year.Value})");
                        }
                        else
                        {
                            candidate.IdentityVerified = true;
                        }
                    }
                    else if (Math.Abs(candidateYear.Value - request.Year.Value) > 1)
                    {
                        reasons.Add($"Year mismatch (found {candidateYear.Value}, expected {request.Year.Value})");
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
                            reasons.Add($"Year mismatch (found {candidateYear.Value}, expected {request.Year.Value})");
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

            if (!candidate.IdentityVerified && !reasons.Any(r => r.Contains("mismatch")))
            {
                reasons.Add("Identity unresolved: unable to verify release identity with Arr or strict title/year match");
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
                    reasons.Add($"Season mismatch (found S{candidate.SeasonNumber:D2}, expected S{targetSeason.Value:D2})");
                }

                if (isFullSeason)
                {
                    if (!candidate.IsSeasonPack)
                    {
                        reasons.Add("Coverage mismatch: requested full season pack, but release is an individual episode");
                    }
                }
                else if (targetEpisode.HasValue)
                {
                    if (candidate.IsSeasonPack)
                    {
                        reasons.Add("Coverage mismatch: requested individual episode, but release is a season pack");
                    }
                    else if (candidate.EpisodeNumber > 0 && candidate.EpisodeNumber != targetEpisode.Value)
                    {
                        reasons.Add($"Episode mismatch (found E{candidate.EpisodeNumber:D2}, expected E{targetEpisode.Value:D2})");
                    }
                    else if (candidate.EpisodeNumber <= 0 && candidate.AbsoluteEpisodeNumber > 0)
                    {
                        // Absolute (anime) numbering: compare against the absolute number Sonarr assigned to the target
                        var targetAbsolute = unit != null && unit.AbsoluteEpisodeNumbers.Count == 1 ? unit.AbsoluteEpisodeNumbers[0] : (int?)null;
                        if (targetAbsolute == null)
                        {
                            reasons.Add("Episode mismatch: release uses absolute numbering but the requested episode has no absolute number in Sonarr");
                        }
                        else if (candidate.AbsoluteEpisodeNumber != targetAbsolute.Value)
                        {
                            reasons.Add($"Episode mismatch (found absolute {candidate.AbsoluteEpisodeNumber}, expected {targetAbsolute.Value})");
                        }
                    }
                    else if (candidate.EpisodeNumber <= 0 && !candidate.IsSeasonPack)
                    {
                        reasons.Add("Episode mismatch: release does not specify episode number for single episode request");
                    }
                }
            }
        }

        // 5. Mandatory: Size limits
        if (config != null)
        {
            if (request.Kind == MediaKind.Movie && config.MaxMovieSizeBytes > 0 && candidate.Size > config.MaxMovieSizeBytes)
            {
                reasons.Add($"Size exceeds movie limit ({candidate.Size} > {config.MaxMovieSizeBytes})");
            }
            else if (request.Kind == MediaKind.Series)
            {
                if (candidate.IsSeasonPack && config.MaxSeasonSizeBytes > 0 && candidate.Size > config.MaxSeasonSizeBytes)
                {
                    reasons.Add($"Size exceeds season limit ({candidate.Size} > {config.MaxSeasonSizeBytes})");
                }
                else if (!candidate.IsSeasonPack && config.MaxEpisodeSizeBytes > 0 && candidate.Size > config.MaxEpisodeSizeBytes)
                {
                    reasons.Add($"Size exceeds episode limit ({candidate.Size} > {config.MaxEpisodeSizeBytes})");
                }
            }
        }

        candidate.IsEligible = reasons.Count == 0;
        candidate.RejectionReasons = reasons.Distinct().ToList();

        // 6. Strict lexicographical tuple scoring (R13)
        var prefRes = config?.PreferredResolution ?? "1080p";
        var prefSource = config?.PreferredSource ?? "WEB-DL";
        var prefCodec = config?.PreferredCodec ?? "H.264";

        var resTier = candidate.Resolution switch
        {
            var r when string.Equals(r, prefRes, StringComparison.OrdinalIgnoreCase) => 4,
            "1080p" => 3,
            "720p" => 2,
            "2160p" => 1,
            _ => 0
        };

        var sourceTier = candidate.Source switch
        {
            var s when string.Equals(s, prefSource, StringComparison.OrdinalIgnoreCase) => 4,
            "WEB-DL" => 3,
            "Bluray" => 2,
            "WEBRip" => 1,
            "HDTV" => 1,
            _ => 0
        };

        var codecTier = candidate.Codec switch
        {
            var c when string.Equals(c, prefCodec, StringComparison.OrdinalIgnoreCase) => 3,
            "H.264" => 2,
            "H.265" => 1,
            _ => 0
        };

        // Resolution (1,000,000) -> Source (100,000) -> Codec (10,000) -> Seeders (max 9,999)
        var score = (resTier * 1_000_000) + (sourceTier * 100_000) + (codecTier * 10_000) + Math.Min(candidate.Seeders, 9999);

        // Bonus for season pack when requesting full season
        if (request.Kind == MediaKind.Series && request.Scope.SeasonNumbers.Count > 0 && request.Scope.EpisodeNumbers.Count == 0 && candidate.IsSeasonPack)
        {
            score += 5_000_000;
        }

        candidate.Score = score;
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
        var cleaned = Regex.Replace(title, @"[._\-\(\)\[\]+:]", " ");
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
