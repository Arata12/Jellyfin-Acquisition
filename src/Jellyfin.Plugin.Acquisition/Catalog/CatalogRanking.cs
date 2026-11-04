using System.Globalization;
using System.Text;
using Jellyfin.Plugin.Acquisition.Controllers.Dtos;
using Jellyfin.Plugin.Acquisition.Models;

namespace Jellyfin.Plugin.Acquisition.Catalog;

/// <summary>
/// Relevance order for external search results: movies and series merged in one list.
/// Base rank is the position inside the item's own source (Sonarr or Radarr); text and year matches pull items forward.
/// </summary>
public static class CatalogRanking
{
    private const double ExactBoost = 100;
    private const double PrefixBoost = 60;
    private const double AllTokensBoost = 40;
    private const double PartialBoost = 20;
    private const double YearBoost = 15;

    public static List<CatalogItemDto> Rank(List<CatalogItemDto> items, string query)
    {
        var queryTokens = Tokenize(query);
        var year = queryTokens.FirstOrDefault(IsYear);
        var textTokens = queryTokens.Count > 1 && year != null ? queryTokens.Where(t => t != year).ToList() : queryTokens;
        var normalizedQuery = string.Join(' ', textTokens);

        var ranksBySource = new Dictionary<MediaKind, int>();
        var scored = new List<(CatalogItemDto Item, double Score, int Index)>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            ranksBySource.TryGetValue(item.Kind, out var rank);
            ranksBySource[item.Kind] = rank + 1;

            var score = -(double)rank;
            if (normalizedQuery.Length > 0)
            {
                score += TextBoost(item, normalizedQuery, textTokens);
            }

            if (year != null && item.Year.HasValue && year == item.Year.Value.ToString(CultureInfo.InvariantCulture))
            {
                score += YearBoost;
            }

            scored.Add((item, score, i));
        }

        return scored.OrderByDescending(x => x.Score).ThenBy(x => x.Index).Select(x => x.Item).ToList();
    }

    private static double TextBoost(CatalogItemDto item, string query, List<string> queryTokens)
    {
        var best = 0d;
        foreach (var title in new[] { item.Title, item.OriginalTitle }.Concat(item.AlternateTitles))
        {
            var tokens = Tokenize(title);
            if (tokens.Count == 0) continue;

            var normalized = string.Join(' ', tokens);
            double boost;
            if (normalized == query)
            {
                boost = ExactBoost;
            }
            else if (normalized.StartsWith(query + " ", StringComparison.Ordinal))
            {
                boost = PrefixBoost;
            }
            else
            {
                var matched = queryTokens.Count(t => tokens.Contains(t));
                boost = matched == queryTokens.Count ? AllTokensBoost : PartialBoost * matched / queryTokens.Count;
            }

            best = Math.Max(best, boost);
        }

        return best;
    }

    private static bool IsYear(string token) => token.Length == 4 && token.All(char.IsAsciiDigit);

    /// <summary>Lowercase, diacritics and punctuation removed; "&amp;" and "and" are dropped as noise.</summary>
    internal static List<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t != "and")
            .ToList();
    }
}
