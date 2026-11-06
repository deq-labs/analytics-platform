namespace AnalyticsPlatform.Api.Ingestion;

// Checks an incoming event. Returns field name -> error messages; empty means valid.
// That dictionary shape is exactly what ASP.NET Core's ValidationProblem response
// expects, so errors go back to the caller as a standard 400 response.
//
// Anyone on the internet can call this endpoint, so the limits below keep a
// single request from stuffing huge strings into the database.
public static class EventRequestValidator
{
    private const int MaxUrlLength = 2048;
    private const int MaxNameLength = 64;
    private const int MaxProperties = 25;
    private const int MaxPropertyKeyLength = 64;
    private const int MaxPropertyValueLength = 512;

    public static Dictionary<string, string[]> Validate(EventRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.SiteId == 0)
        {
            errors["siteId"] = ["Required."];
        }

        if (!IsHttpUrl(request.Url, out _))
        {
            errors["url"] = ["Must be an absolute http(s) URL."];
        }

        if (!string.IsNullOrEmpty(request.Referrer) && !IsHttpUrl(request.Referrer, out _))
        {
            errors["referrer"] = ["Must be an absolute http(s) URL when present."];
        }

        ValidateTypeAndName(request, errors);
        ValidateProperties(request, errors);

        return errors;
    }

    private static void ValidateTypeAndName(EventRequest request, Dictionary<string, string[]> errors)
    {
        var name = request.Name ?? "";

        switch (request.Type)
        {
            case EventTypes.Pageview:
                if (name.Length > 0)
                {
                    errors["name"] = ["Pageviews don't have a name."];
                }
                break;

            case EventTypes.Standard:
                if (!EventTypes.StandardEventNames.Contains(name))
                {
                    errors["name"] = [$"Unknown standard event '{name}'."];
                }
                break;

            case EventTypes.Custom:
                if (name.Length is 0 or > MaxNameLength)
                {
                    errors["name"] = [$"Required, at most {MaxNameLength} characters."];
                }
                else if (EventTypes.StandardEventNames.Contains(name))
                {
                    errors["name"] = [$"'{name}' is reserved for a standard event."];
                }
                break;

            default:
                errors["type"] = ["Must be 'pageview', 'standard', or 'custom'."];
                break;
        }
    }

    private static void ValidateProperties(EventRequest request, Dictionary<string, string[]> errors)
    {
        if (request.Properties is not { Count: > 0 } properties)
        {
            return;
        }

        if (request.Type == EventTypes.Pageview)
        {
            errors["properties"] = ["Pageviews don't have properties."];
        }
        else if (properties.Count > MaxProperties)
        {
            errors["properties"] = [$"At most {MaxProperties} properties."];
        }
        else if (properties.Any(p => p.Key.Length is 0 or > MaxPropertyKeyLength
                                     || p.Value.Length > MaxPropertyValueLength))
        {
            errors["properties"] =
                [$"Keys must be 1-{MaxPropertyKeyLength} characters, values at most {MaxPropertyValueLength}."];
        }
    }

    public static bool IsHttpUrl(string? value, out Uri uri)
    {
        uri = null!;
        return value is { Length: > 0 and <= MaxUrlLength }
               && Uri.TryCreate(value, UriKind.Absolute, out uri!)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
