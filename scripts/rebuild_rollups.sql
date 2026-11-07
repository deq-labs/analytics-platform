-- Rebuilds pageviews_hourly from the raw events table. Run once on a database
-- that had events before clickhouse/init/002_rollups.sql was applied:
--
--   docker exec -i analytics-clickhouse clickhouse-client \
--     --user analytics --password ... --multiquery < scripts/rebuild_rollups.sql
--
-- Events that arrive while this runs may be counted twice in their hour (the
-- materialized view adds them too), so run it when ingestion is quiet.

TRUNCATE TABLE analytics.pageviews_hourly;

INSERT INTO analytics.pageviews_hourly
SELECT site_id, toStartOfHour(timestamp) AS hour, count(), uniqState(visitor_id)
FROM analytics.events
WHERE event_type = 'pageview' AND is_bot = 0
GROUP BY site_id, hour;
