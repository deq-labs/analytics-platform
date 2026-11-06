namespace AnalyticsPlatform.Api.Ingestion;

// The JSON body the tracking snippet sends to POST /api/events, e.g.
//   { "siteId": 1, "type": "pageview", "url": "https://example.com/pricing",
//     "referrer": "https://google.com/", "screenWidth": 1920, "screenHeight": 1080 }
// ASP.NET Core turns the JSON into this record automatically (camelCase JSON
// names map onto these PascalCase properties). `?` means the field is optional.
public sealed record EventRequest(
    uint SiteId,
    string Type,
    string? Name,
    string Url,
    string? Referrer,
    string? VisitorId,
    ushort? ScreenWidth,
    ushort? ScreenHeight,
    Dictionary<string, string>? Properties);
