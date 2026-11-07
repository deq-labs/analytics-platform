using System.Globalization;

namespace AnalyticsPlatform.Api.Stats;

// What a breakdown groups by. Each maps to a fixed column in StatsQueries --
// the user's text never becomes part of the SQL.
public enum BreakdownDimension
{
    Browser,
    Os,
    Device,
    Country,
}

public enum StatsInterval
{
    Hour,
    Day,
}

// A validated, UTC time range: From inclusive, To exclusive.
// `readonly record struct` = a small value type (copied, not referenced),
// with record conveniences. Good fit for two dates.
public readonly record struct DateRange(DateTime From, DateTime To);

// Query-string parameters shared by every stats endpoint, e.g.
//   ?from=2026-11-01&to=2026-11-08&interval=day&limit=10&dimension=browser
// Endpoints take this with [AsParameters], which binds each property from the
// query string by name.
//
// Dates come in as strings on purpose: parsing them ourselves guarantees a date
// without a timezone ("2026-11-01") is treated as UTC, not as the server's local time.
public sealed record StatsRequest(string? From, string? To, string? Interval, int? Limit, string? Dimension)
{
    private static readonly TimeSpan DefaultRange = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);
    private static readonly TimeSpan MaxHourlyRange = TimeSpan.FromDays(31); // 744 points
    private const int DefaultLimit = 10;
    private const int MaxLimit = 100;

    // Defaults: the last 7 days up to now.
    public bool TryGetRange(TimeProvider timeProvider, Dictionary<string, string[]> errors, out DateRange range)
    {
        range = default;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (!TryParseUtc(To, "to", now, errors, out var to) ||
            !TryParseUtc(From, "from", to - DefaultRange, errors, out var from))
        {
            return false;
        }

        if (from >= to)
        {
            errors["from"] = ["Must be before 'to'."];
            return false;
        }

        if (to - from > MaxRange)
        {
            errors["from"] = ["Range can be at most 366 days."];
            return false;
        }

        range = new DateRange(from, to);
        return true;
    }

    public bool TryGetInterval(DateRange range, Dictionary<string, string[]> errors, out StatsInterval interval)
    {
        // `switch` expression with a `when`-free pattern list; `_` is "anything else".
        (var ok, interval) = Interval?.ToLowerInvariant() switch
        {
            null or "day" => (true, StatsInterval.Day),
            "hour" => (true, StatsInterval.Hour),
            _ => (false, StatsInterval.Day),
        };

        if (!ok)
        {
            errors["interval"] = ["Must be 'hour' or 'day'."];
            return false;
        }

        if (interval == StatsInterval.Hour && range.To - range.From > MaxHourlyRange)
        {
            errors["interval"] = ["Hourly data is limited to ranges of 31 days or less."];
            return false;
        }

        return true;
    }

    public bool TryGetDimension(Dictionary<string, string[]> errors, out BreakdownDimension dimension)
    {
        (var ok, dimension) = Dimension?.ToLowerInvariant() switch
        {
            "browser" => (true, BreakdownDimension.Browser),
            "os" => (true, BreakdownDimension.Os),
            "device" => (true, BreakdownDimension.Device),
            "country" => (true, BreakdownDimension.Country),
            _ => (false, default),
        };

        if (!ok)
        {
            errors["dimension"] = ["Must be 'browser', 'os', 'device', or 'country'."];
        }

        return ok;
    }

    public bool TryGetLimit(Dictionary<string, string[]> errors, out int limit)
    {
        limit = Limit ?? DefaultLimit;
        if (limit is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"Must be between 1 and {MaxLimit}."];
            return false;
        }

        return true;
    }

    private static bool TryParseUtc(
        string? value, string name, DateTime fallback, Dictionary<string, string[]> errors, out DateTime result)
    {
        if (string.IsNullOrEmpty(value))
        {
            result = fallback;
            return true;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result))
        {
            return true;
        }

        errors[name] = ["Must be an ISO 8601 date, e.g. 2026-11-01 or 2026-11-01T12:00:00Z."];
        return false;
    }
}
