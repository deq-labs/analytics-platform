-- Retention policy (Phase 8, 2026-11-07), replacing the 1-year placeholder.
--
--   raw events        13 months -- every report (top pages, referrers,
--                                  breakdowns) works on any range up to the
--                                  366-day maximum, plus a month of slack
--   pageviews_hourly  5 years   -- long-term traffic trend, tiny (002_rollups.sql)
--
-- TTL is enforced by background merges: expired rows disappear within hours,
-- not at the exact second. Changing it later is a metadata-only ALTER.
-- Safe to run more than once.

ALTER TABLE analytics.events MODIFY TTL toDateTime(timestamp) + INTERVAL 13 MONTH DELETE;
