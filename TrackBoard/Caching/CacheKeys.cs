using System.Globalization;
using TrackBoard.Entities;

namespace TrackBoard.Caching;

public static class CacheKeys
{
    /// <summary>
    /// Bumped when a cached payload's shape changes. Without it, a deployment that alters
    /// a response record would read back stale entries that no longer deserialise.
    /// </summary>
    private const string Version = "v1";

    /// <summary>
    /// Includes the season as well as the id: the id alone is unique, but a self-describing
    /// key survives a database restore that reuses ids across environments, and it makes a
    /// stray entry obvious when reading keys out of Redis.
    /// </summary>
    public static string Leaderboard(Guid seriesId, int season) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"trackboard:{Version}:leaderboard:series:{seriesId}:season:{season}");

    /// <summary>
    /// Tag applied to every entry derived from a series, so a single
    /// <c>RemoveByTagAsync</c> clears them all without knowing each key.
    /// </summary>
    public static string SeriesTag(Guid seriesId) =>
        string.Create(CultureInfo.InvariantCulture, $"series:{seriesId}");

    /// <summary>
    /// Every driver's best ranked lap on one track. The full list is cached rather than the
    /// top N, so the caller's own position and a lap's rank come from the same entry.
    /// </summary>
    public static string TrackStandings(Guid trackId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"trackboard:{Version}:standings:track:{trackId}");

    /// <summary>
    /// Every car's best time on one drag metric. One entry serves every class filter, so a
    /// class view and the overall board can never disagree.
    /// </summary>
    public static string DragStandings(DragMetric metric) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"trackboard:{Version}:standings:drag:{metric}");

    /// <summary>Tag on every drag board. There are few of them, so they all clear together.</summary>
    public const string DragTag = "drag";

    /// <summary>
    /// An event's live board. Also tagged with its track, so any lap uploaded on that track
    /// evicts it without the session code knowing events exist.
    /// </summary>
    public static string EventBoard(Guid eventId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"trackboard:{Version}:board:event:{eventId}");

    /// <summary>Tag on every entry derived from an event's entries or groups.</summary>
    public static string EventTag(Guid eventId) =>
        string.Create(CultureInfo.InvariantCulture, $"event:{eventId}");

    /// <summary>Tag on every entry derived from a track's laps.</summary>
    public static string TrackTag(Guid trackId) =>
        string.Create(CultureInfo.InvariantCulture, $"track:{trackId}");
}
