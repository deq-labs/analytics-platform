using System.Collections.Frozen;

namespace AnalyticsPlatform.Api.Ingestion;

public static class EventTypes
{
    public const string Pageview = "pageview";
    public const string Standard = "standard";
    public const string Custom = "custom";

    // The auto-tracked "standard" events (see CLAUDE.md). Keep in sync with the
    // CHECK constraint on site_tracked_events.event_key in postgres/init/001_sites.sql.
    // Custom events may not use these names.
    //
    // FrozenSet is a read-only set optimized for lookups that never change.
    public static readonly FrozenSet<string> StandardEventNames = new[]
    {
        "scroll", "outbound_click", "file_download",
        "form_start", "form_submit",
        "video_start", "video_progress", "video_complete",
    }.ToFrozenSet();
}
