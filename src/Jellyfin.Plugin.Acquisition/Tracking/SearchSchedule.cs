using Jellyfin.Plugin.Acquisition.Adapters.Radarr.Models;

namespace Jellyfin.Plugin.Acquisition.Tracking;

/// <summary>A reason not to search yet and when to look again.</summary>
public sealed record SearchWait(string Note, DateTimeOffset RetryAt);

/// <summary>
/// When searching makes sense: not before a movie is out digitally, and with growing pauses between empty searches
/// so private trackers are not hammered (user decision).
/// </summary>
public static class SearchSchedule
{
    /// <summary>A film released in cinemas this recently is probably not on the indexers yet.</summary>
    public static readonly TimeSpan RecentCinemaWindow = TimeSpan.FromDays(90);

    /// <summary>How often a movie with no digital date is checked again.</summary>
    public static readonly TimeSpan CinemaRecheck = TimeSpan.FromHours(24);

    /// <summary>
    /// Pause before the next search after an empty one, counted from the first empty search of the run:
    /// 5 minutes during the first hour, then hourly until 24 hours, then every 6 hours.
    /// </summary>
    public static TimeSpan EmptySearchDelay(TimeSpan sinceFirstEmpty)
    {
        if (sinceFirstEmpty < TimeSpan.FromHours(1)) return TimeSpan.FromMinutes(5);
        if (sinceFirstEmpty < TimeSpan.FromHours(24)) return TimeSpan.FromHours(1);
        return TimeSpan.FromHours(6);
    }

    /// <summary>
    /// Null when the movie can be searched now. A known digital release in the future waits for it; a past digital
    /// release means searching; without a digital date the physical release is used the same way. With neither, a
    /// cinema release in the future or in the last 90 days waits (re-checked every 24 hours).
    /// </summary>
    public static SearchWait? ForMovie(RadarrMovie movie, DateTimeOffset now) =>
        ForMovie(movie.InCinemas, movie.DigitalRelease, movie.PhysicalRelease, now);

    public static SearchWait? ForMovie(DateTime? inCinemas, DateTime? digital, DateTime? physical, DateTimeOffset now)
    {
        var digitalDate = ToUtc(digital);
        if (digitalDate.HasValue)
        {
            return digitalDate.Value > now
                ? new SearchWait($"Lanzamiento digital el {digitalDate.Value:dd/MM/yyyy}; se buscará a partir de esa fecha.", digitalDate.Value)
                : null;
        }

        var physicalDate = ToUtc(physical);
        if (physicalDate.HasValue)
        {
            return physicalDate.Value > now
                ? new SearchWait($"Lanzamiento en físico el {physicalDate.Value:dd/MM/yyyy}; se buscará a partir de esa fecha.", physicalDate.Value)
                : null;
        }

        var cinemaDate = ToUtc(inCinemas);
        if (cinemaDate.HasValue)
        {
            if (cinemaDate.Value > now)
            {
                return new SearchWait($"Estreno en cines el {cinemaDate.Value:dd/MM/yyyy}; se buscará cuando salga en digital.", now + CinemaRecheck);
            }

            if (now - cinemaDate.Value <= RecentCinemaWindow)
            {
                return new SearchWait("Estreno en cines reciente; se buscará cuando salga en digital.", now + CinemaRecheck);
            }
        }

        return null;
    }

    private static DateTimeOffset? ToUtc(DateTime? date)
    {
        if (!date.HasValue) return null;
        var value = date.Value.Kind switch
        {
            DateTimeKind.Local => date.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(date.Value, DateTimeKind.Utc)
        };
        return new DateTimeOffset(value);
    }
}
