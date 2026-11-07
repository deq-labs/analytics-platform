using System.Net;
using MaxMind.Db;
using MaxMind.GeoIP2;
using Microsoft.Extensions.Options;

namespace AnalyticsPlatform.Api.Enrichment;

// Where an IP address is. Region is the most specific subdivision (state,
// province, ...), names in English. All "" when unknown.
public sealed record GeoLocation(string CountryCode, string Region, string City)
{
    public static readonly GeoLocation Unknown = new("", "", "");
}

public interface IGeoLocator
{
    GeoLocation Locate(IPAddress? ipAddress);
}

// Bound from the "GeoIp" config section. Relative paths are resolved against
// the API's content root (see Program.cs).
public sealed class GeoIpOptions
{
    public string DatabasePath { get; set; } = "";
}

// Looks IPs up in MaxMind's GeoLite2-City database file, which the
// geoipupdate container downloads and replaces every few days (docker-compose.yml).
//
// * Geo is optional: no file configured / file missing -> everything Unknown,
//   never an error. Events still flow.
// * The file is loaded fully into memory (FileAccessMode.Memory, ~60 MB) rather
//   than memory-mapped: on Windows a mapped file is locked, and geoipupdate
//   could then never replace it.
// * Every ReloadCheckInterval it checks the file's timestamp and swaps in a new
//   reader if the file changed (or appeared).
// * The IP is only used for the lookup; it's never stored or logged.
public sealed class MaxMindGeoLocator(
    IOptions<GeoIpOptions> options,
    TimeProvider timeProvider,
    ILogger<MaxMindGeoLocator> logger) : IGeoLocator, IDisposable
{
    public static readonly TimeSpan ReloadCheckInterval = TimeSpan.FromMinutes(5);

    private readonly string _path = options.Value.DatabasePath;
    private readonly Lock _reloadLock = new();

    // `volatile`: request threads read this while a reload may be swapping it.
    private volatile DatabaseReader? _reader;
    private DateTime _loadedFileTimeUtc;
    private long _nextCheckTicks; // when to look at the file again (UTC ticks)
    private bool _reportedMissing;

    public GeoLocation Locate(IPAddress? ipAddress)
    {
        if (ipAddress is null)
        {
            return GeoLocation.Unknown;
        }

        ReloadIfDue();
        var reader = _reader;
        if (reader is null)
        {
            return GeoLocation.Unknown;
        }

        if (ipAddress.IsIPv4MappedToIPv6)
        {
            ipAddress = ipAddress.MapToIPv4();
        }

        try
        {
            if (!reader.TryCity(ipAddress, out var response) || response is null)
            {
                return GeoLocation.Unknown; // private, loopback, or not in the database
            }

            var country = response.Country.IsoCode ?? "";
            return new GeoLocation(
                CountryCode: country.Length == 2 ? country : "", // column is FixedString(2)
                Region: response.MostSpecificSubdivision.Name ?? "",
                City: response.City.Name ?? "");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GeoIP lookup failed."); // deliberately without the IP
            return GeoLocation.Unknown;
        }
    }

    private void ReloadIfDue()
    {
        var now = timeProvider.GetUtcNow().UtcTicks;
        if (now < Volatile.Read(ref _nextCheckTicks))
        {
            return; // fast path: almost every call
        }

        lock (_reloadLock)
        {
            if (now < _nextCheckTicks)
            {
                return; // another thread just checked
            }

            Volatile.Write(ref _nextCheckTicks, now + ReloadCheckInterval.Ticks);

            if (string.IsNullOrWhiteSpace(_path))
            {
                return; // GeoIP not configured
            }

            var file = new FileInfo(_path);
            if (!file.Exists)
            {
                if (!_reportedMissing)
                {
                    logger.LogInformation("GeoIP database not found at {Path}; geo columns stay empty until it appears.", _path);
                    _reportedMissing = true;
                }

                return; // keep using the old reader, if any
            }

            if (_reader is not null && file.LastWriteTimeUtc == _loadedFileTimeUtc)
            {
                return; // unchanged
            }

            try
            {
                // Swap, don't dispose the old reader: requests may still be using
                // it. In Memory mode it's just a byte array the GC will collect.
                _reader = new DatabaseReader(_path, FileAccessMode.Memory);
                _loadedFileTimeUtc = file.LastWriteTimeUtc;
                _reportedMissing = false;
                logger.LogInformation("Loaded GeoIP database {Path} (built {BuildDate:yyyy-MM-dd}).",
                    _path, _reader.Metadata.BuildDate);
            }
            catch (Exception ex)
            {
                // E.g. the file is mid-download. Try again next interval.
                logger.LogWarning(ex, "Could not load GeoIP database {Path}; will retry.", _path);
            }
        }
    }

    public void Dispose() => _reader?.Dispose();
}
