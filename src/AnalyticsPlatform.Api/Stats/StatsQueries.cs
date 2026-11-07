using ClickHouse.Driver;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.Utility; // AddParameter() extension method

namespace AnalyticsPlatform.Api.Stats;

// The ClickHouse queries behind the dashboard. Each one filters on
// (site_id, event_type, timestamp) -- the table's sort key, in that order -- so
// ClickHouse can skip straight to the relevant rows instead of scanning the table.
//
// Values are passed as ClickHouse query parameters ({name:Type}), never pasted
// into the SQL. The only thing that changes the SQL text itself is the interval,
// and that comes from our own enum, never from user input.
public sealed class StatsQueries(ClickHouseClient client)
{
    // Shared WHERE clause. $$""" is a raw string where {{x}} is C# interpolation,
    // so ClickHouse's single-brace {param:Type} placeholders can be written as-is.
    private const string PageviewFilter = """
        site_id = {site:UInt32}
          AND event_type = 'pageview'
          AND timestamp >= {from:DateTime64(3, 'UTC')}
          AND timestamp <  {to:DateTime64(3, 'UTC')}
          AND is_bot = 0
        """;

    public async Task<IReadOnlyList<TimeSeriesPoint>> PageviewsOverTimeAsync(
        uint siteId, DateRange range, StatsInterval interval, CancellationToken cancellationToken)
    {
        var (bucket, step) = interval switch
        {
            StatsInterval.Hour => ("toStartOfHour", "INTERVAL 1 HOUR"),
            _ => ("toStartOfDay", "INTERVAL 1 DAY"),
        };

        // uniq() is an approximate distinct count -- very fast, typically within
        // ~1-2%. uniqExact() would be exact but much more memory-hungry.
        // WITH FILL inserts the hours/days that had no events (as zeros), so a
        // chart doesn't silently skip quiet days.
        var sql = $$"""
            SELECT {{bucket}}(timestamp) AS bucket,
                   count() AS pageviews,
                   uniq(visitor_id) AS visitors
            FROM events
            WHERE {{PageviewFilter}}
            GROUP BY bucket
            ORDER BY bucket WITH FILL
                FROM {{bucket}}({from:DateTime64(3, 'UTC')})
                TO toDateTime({to:DateTime64(3, 'UTC')})
                STEP {{step}}
            """;

        var points = new List<TimeSeriesPoint>();
        using var reader = await client.ExecuteReaderAsync(
            sql, Parameters(siteId, range), cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            points.Add(new TimeSeriesPoint(
                DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                reader.GetFieldValue<ulong>(1),
                reader.GetFieldValue<ulong>(2)));
        }

        return points;
    }

    public async Task<IReadOnlyList<TopPage>> TopPagesAsync(
        uint siteId, DateRange range, int limit, CancellationToken cancellationToken)
    {
        // `top_n`, not `limit`: a parameter named "limit" clashes with ClickHouse's
        // built-in `limit` setting.
        const string sql = $$"""
            SELECT path, count() AS pageviews, uniq(visitor_id) AS visitors
            FROM events
            WHERE {{PageviewFilter}}
            GROUP BY path
            ORDER BY pageviews DESC, path
            LIMIT {top_n:UInt32}
            """;

        var parameters = Parameters(siteId, range);
        parameters.AddParameter("top_n", (uint)limit);

        var pages = new List<TopPage>();
        using var reader = await client.ExecuteReaderAsync(sql, parameters, cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            pages.Add(new TopPage(reader.GetString(0), reader.GetFieldValue<ulong>(1), reader.GetFieldValue<ulong>(2)));
        }

        return pages;
    }

    public async Task<IReadOnlyList<TopReferrer>> TopReferrersAsync(
        uint siteId, DateRange range, int limit, CancellationToken cancellationToken)
    {
        // Referrers are counted per *session*, using each session's first
        // pageview: that's where the visitor came from. (Later pageviews in a
        // session are internal navigation.)
        //
        // Inner query: one row per session. argMin(x, timestamp) = "the value of
        // x on the row with the smallest timestamp", i.e. the entry pageview.
        // A referrer from the site itself (e.g. a session that resumed after 30
        // idle minutes) counts as direct.
        const string sql = $$"""
            SELECT source, count() AS sessions, uniq(visitor_id) AS visitors
            FROM
            (
                SELECT any(visitor_id) AS visitor_id,
                       argMin(referrer_domain, timestamp) AS first_referrer,
                       argMin(hostname, timestamp) AS first_hostname,
                       if(first_referrer = replaceRegexpOne(first_hostname, '^www\\.', ''),
                          '', first_referrer) AS source
                FROM events
                WHERE {{PageviewFilter}}
                GROUP BY session_id
            )
            GROUP BY source
            ORDER BY sessions DESC, source
            LIMIT {top_n:UInt32}
            """;

        var parameters = Parameters(siteId, range);
        parameters.AddParameter("top_n", (uint)limit);

        var referrers = new List<TopReferrer>();
        using var reader = await client.ExecuteReaderAsync(sql, parameters, cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var domain = reader.GetString(0);
            referrers.Add(new TopReferrer(
                domain.Length == 0 ? null : domain,
                reader.GetFieldValue<ulong>(1),
                reader.GetFieldValue<ulong>(2)));
        }

        return referrers;
    }

    public async Task<IReadOnlyList<BreakdownItem>> BreakdownAsync(
        uint siteId, DateRange range, BreakdownDimension dimension, int limit, CancellationToken cancellationToken)
    {
        // The column comes from our enum, never from the request text.
        // country_code is FixedString(2); toString makes it a normal string.
        var column = dimension switch
        {
            BreakdownDimension.Browser => "browser",
            BreakdownDimension.Os => "os",
            BreakdownDimension.Device => "device_type",
            BreakdownDimension.Country => "toString(country_code)",
            _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
        };

        var sql = $$"""
            SELECT {{column}} AS value, uniq(visitor_id) AS visitors, count() AS pageviews
            FROM events
            WHERE {{PageviewFilter}}
            GROUP BY value
            ORDER BY visitors DESC, pageviews DESC, value
            LIMIT {top_n:UInt32}
            """;

        var parameters = Parameters(siteId, range);
        parameters.AddParameter("top_n", (uint)limit);

        var items = new List<BreakdownItem>();
        using var reader = await client.ExecuteReaderAsync(sql, parameters, cancellationToken: cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = reader.GetString(0);
            items.Add(new BreakdownItem(
                value.Length == 0 ? null : value,
                reader.GetFieldValue<ulong>(1),
                reader.GetFieldValue<ulong>(2)));
        }

        return items;
    }

    private static ClickHouseParameterCollection Parameters(uint siteId, DateRange range)
    {
        var parameters = new ClickHouseParameterCollection();
        parameters.AddParameter("site", siteId);
        parameters.AddParameter("from", range.From);
        parameters.AddParameter("to", range.To);
        return parameters;
    }
}
