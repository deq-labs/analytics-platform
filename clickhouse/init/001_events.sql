-- Core event store. Append-only; every pageview, standard (auto-tracked), and
-- custom event lands here.
-- Design notes:
--   * PARTITION BY month keeps partitions manageable at this scale and lines up
--     with how retention/rollups (step 8) will typically expire whole months.
--   * ORDER BY (site_id, event_type, timestamp) because nearly every query filters
--     by site_id first, then a time range. This is also the primary key (no
--     separate PRIMARY KEY given, so ClickHouse uses the sort key).
--   * Browser/OS/geo columns exist now even though enrichment (UA parsing, GeoIP)
--     doesn't land until step 6 -- they'll just be empty strings until then. Adding
--     columns later is a lightweight ALTER; getting the *shape* right now avoids a
--     second pass over the whole design.
--   * `properties` uses Map(String, String) rather than the native JSON type.
--     ClickHouse's JSON type (production-ready since 25.3) is the better fit for
--     deeply nested / mixed-type payloads, but it comes with more config surface
--     (path limits, dynamic type tuning). For custom event properties, which are
--     flat and mostly used for grouping/filtering rather than complex reads, Map
--     is simpler and cheaper to write. Revisit if custom event schemas get nested.
--   * `event_type = 'standard'` covers GA-Enhanced-Measurement-style auto-tracked
--     interaction events (scroll, outbound_click, file_download, form_start,
--     form_submit, video_start, video_progress, video_complete) -- see CLAUDE.md
--     "Standard (auto-tracked) events" for the full reference list and the
--     per-site on/off toggle design. They reuse `event_name` + `properties`
--     rather than adding dedicated columns, same as custom events. Enforcing
--     that custom event names don't collide with the reserved standard names is
--     an ingestion-API concern (Phase 2), not a DB-level constraint here.

CREATE TABLE IF NOT EXISTS analytics.events
(
    -- identity
    site_id       UInt32,
    event_type    LowCardinality(String),           -- 'pageview' | 'standard' | 'custom'
    event_name    LowCardinality(String) DEFAULT '', -- standard/custom event name; empty for pageviews

    -- timing
    timestamp     DateTime64(3, 'UTC'),

    -- session / visitor
    session_id    UUID,
    visitor_id    String,                            -- hashed anonymous identifier

    -- page
    url             String,
    path            String,
    hostname        LowCardinality(String),

    -- referrer / campaign
    referrer         String DEFAULT '',
    referrer_domain  LowCardinality(String) DEFAULT '',
    utm_source       LowCardinality(String) DEFAULT '',
    utm_medium       LowCardinality(String) DEFAULT '',
    utm_campaign     LowCardinality(String) DEFAULT '',
    utm_term         LowCardinality(String) DEFAULT '',
    utm_content      LowCardinality(String) DEFAULT '',

    -- client (raw captured now, parsed columns filled in starting step 6)
    user_agent       String DEFAULT '',
    browser          LowCardinality(String) DEFAULT '',
    browser_version  LowCardinality(String) DEFAULT '',
    os               LowCardinality(String) DEFAULT '',
    os_version       LowCardinality(String) DEFAULT '',
    device_type      LowCardinality(String) DEFAULT '', -- 'desktop' | 'mobile' | 'tablet'
    screen_width     UInt16 DEFAULT 0,
    screen_height    UInt16 DEFAULT 0,

    -- geo (filled in starting step 6, via GeoLite2)
    country_code     LowCardinality(FixedString(2)) DEFAULT '',
    region           LowCardinality(String) DEFAULT '',
    city             String DEFAULT '',

    -- misc
    ip_hash          String DEFAULT '',   -- hashed at ingestion, never store raw IPs
    is_bot           UInt8 DEFAULT 0,

    -- custom event payload
    properties       Map(String, String) DEFAULT map()
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(timestamp)
ORDER BY (site_id, event_type, timestamp)
TTL toDateTime(timestamp) + INTERVAL 1 YEAR DELETE  -- replaced by 003_retention.sql (13 months).
                                                     -- ALTER TABLE ... MODIFY TTL is cheap to change later,
                                                     -- it doesn't require rewriting the table.
SETTINGS index_granularity = 8192;
