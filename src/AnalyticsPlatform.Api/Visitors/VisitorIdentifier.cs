using System.Net;
using System.Security.Cryptography;
using System.Text;
using AnalyticsPlatform.Api.Sites;

namespace AnalyticsPlatform.Api.Visitors;

public sealed record VisitorIdentity(string VisitorId, string IpHash);

// Works out who sent an event, per the site's visitor_id_mode.
// The raw IP address is used only inside this method and is never stored or logged.
public sealed class VisitorIdentifier(IDailySaltProvider saltStore)
{
    private static readonly byte[] Separator = [0];

    public async Task<VisitorIdentity> IdentifyAsync(
        SiteConfig site,
        IPAddress? ipAddress,
        string userAgent,
        string? clientVisitorId,
        CancellationToken cancellationToken)
    {
        var salt = await saltStore.GetTodaysSaltAsync(cancellationToken);
        var ip = NormalizeIp(ipAddress);

        var ipHash = Hash(salt, ip);

        // Persistent mode uses the browser's ID only if it's a real UUID.
        // Anything else (missing, garbage) falls back to the cookie-less hash.
        var visitorId =
            site.VisitorIdMode == VisitorIdMode.Persistent && Guid.TryParse(clientVisitorId, out var browserId)
                ? $"p:{browserId:D}"
                : $"h:{Hash(salt, site.Id.ToString(), ip, userAgent)}";

        return new VisitorIdentity(visitorId, ipHash);
    }

    // The same IPv4 address can arrive written as "::ffff:1.2.3.4" (IPv6 form);
    // convert it so one visitor doesn't get two different hashes.
    private static string NormalizeIp(IPAddress? ipAddress) =>
        ipAddress switch
        {
            null => "",
            { IsIPv4MappedToIPv6: true } => ipAddress.MapToIPv4().ToString(),
            _ => ipAddress.ToString(),
        };

    // SHA-256 over: salt, then each part followed by a 0 byte. The separator
    // stops ("ab", "c") and ("a", "bc") from producing the same hash.
    private static string Hash(byte[] salt, params ReadOnlySpan<string> parts)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha256.AppendData(salt);
        foreach (var part in parts)
        {
            sha256.AppendData(Encoding.UTF8.GetBytes(part));
            sha256.AppendData(Separator);
        }

        return Convert.ToHexStringLower(sha256.GetHashAndReset());
    }
}
