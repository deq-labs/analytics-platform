namespace AnalyticsPlatform.Api.Stats;

// Shapes of the JSON the stats endpoints return. ClickHouse count() and uniq()
// return UInt64, which is `ulong` in C#.

public sealed record TimeSeriesPoint(DateTime Time, ulong Pageviews, ulong Visitors);

public sealed record TopPage(string Path, ulong Pageviews, ulong Visitors);

// Domain is null for direct traffic (no referrer, or a referrer from the site itself).
public sealed record TopReferrer(string? Domain, ulong Sessions, ulong Visitors);

public sealed record TimeSeriesResponse(
    DateTime From, DateTime To, string Interval, IReadOnlyList<TimeSeriesPoint> Points);

// A *generic* record: one definition works for any item type, so
// TopListResponse<TopPage> and TopListResponse<TopReferrer> share it.
public sealed record TopListResponse<T>(DateTime From, DateTime To, IReadOnlyList<T> Items);
