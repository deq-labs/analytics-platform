-- Hourly pageview rollup (Phase 8, 2026-11-07).
--
-- The dashboard's time-series chart used to scan every raw pageview in the
-- range. Instead, a *materialized view* pre-aggregates each insert into one
-- row per (site, hour) as the data arrives, and the chart reads those rows:
-- ~24 rows per site per day instead of one row per pageview.
--
--   pageviews  SimpleAggregateFunction(sum, ...) -- merges by adding
--   visitors   AggregateFunction(uniq, String)   -- a uniq() *state*, not a number:
--              states merge correctly, so "visitors per day" = uniqMerge over
--              that day's 24 hours counts someone seen in two hours once.
--
-- AggregatingMergeTree merges rows with the same sort key in the background,
-- so a query must still aggregate (sum / uniqMerge ... GROUP BY) because rows
-- for one hour may not have been merged yet.
--
-- Only human pageviews are rolled up (is_bot = 0), the same filter every stats
-- query uses. Kept 5 years (raw events: 13 months, see 003_retention.sql).
--
-- A materialized view only sees rows inserted AFTER it exists. On a database
-- that already has events, run scripts/rebuild_rollups.sql once.

CREATE TABLE IF NOT EXISTS analytics.pageviews_hourly
(
    site_id    UInt32,
    hour       DateTime('UTC'),
    pageviews  SimpleAggregateFunction(sum, UInt64),
    visitors   AggregateFunction(uniq, String)
)
ENGINE = AggregatingMergeTree
PARTITION BY toYYYYMM(hour)
ORDER BY (site_id, hour)
TTL hour + INTERVAL 5 YEAR DELETE;

CREATE MATERIALIZED VIEW IF NOT EXISTS analytics.pageviews_hourly_mv
TO analytics.pageviews_hourly
AS SELECT
    site_id,
    toStartOfHour(timestamp) AS hour,
    count() AS pageviews,
    uniqState(visitor_id) AS visitors
FROM analytics.events
WHERE event_type = 'pageview' AND is_bot = 0
GROUP BY site_id, hour;
