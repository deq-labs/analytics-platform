-- Run these by hand with clickhouse-client to sanity check the schema before any C#
-- code touches it (password is CLICKHOUSE_PASSWORD from your .env):
--
--   docker exec -it analytics-clickhouse clickhouse-client \
--     --user analytics --password <your password> --database analytics
--
-- Then paste blocks from this file one at a time.

-- 1. Insert a handful of fake pageviews across a few days/pages/referrers so the
--    aggregation queries below have something to chew on.

INSERT INTO events
    (site_id, event_type, timestamp, session_id, visitor_id, url, path, hostname,
     referrer, referrer_domain, user_agent, device_type)
VALUES
    (1, 'pageview', now() - INTERVAL 2 DAY, generateUUIDv4(), 'visitor-a',
     'https://example.com/', '/', 'example.com',
     '', '', 'Mozilla/5.0', 'desktop'),
    (1, 'pageview', now() - INTERVAL 2 DAY, generateUUIDv4(), 'visitor-a',
     'https://example.com/pricing', '/pricing', 'example.com',
     'https://google.com/search', 'google.com', 'Mozilla/5.0', 'desktop'),
    (1, 'pageview', now() - INTERVAL 1 DAY, generateUUIDv4(), 'visitor-b',
     'https://example.com/', '/', 'example.com',
     'https://twitter.com/', 'twitter.com', 'Mozilla/5.0 Mobile', 'mobile'),
    (1, 'pageview', now() - INTERVAL 1 DAY, generateUUIDv4(), 'visitor-b',
     'https://example.com/blog/hello-world', '/blog/hello-world', 'example.com',
     '', '', 'Mozilla/5.0 Mobile', 'mobile'),
    (1, 'pageview', now(), generateUUIDv4(), 'visitor-c',
     'https://example.com/pricing', '/pricing', 'example.com',
     'https://google.com/search', 'google.com', 'Mozilla/5.0', 'desktop');

-- 2. Pageviews over time (the time-series chart query) --------------------------
SELECT
    toDate(timestamp) AS day,
    count() AS pageviews
FROM events
WHERE site_id = 1
  AND event_type = 'pageview'
  AND timestamp >= now() - INTERVAL 30 DAY
GROUP BY day
ORDER BY day;

-- 3. Top pages -------------------------------------------------------------------
SELECT
    path,
    count() AS pageviews,
    uniqExact(visitor_id) AS unique_visitors
FROM events
WHERE site_id = 1
  AND event_type = 'pageview'
  AND timestamp >= now() - INTERVAL 30 DAY
GROUP BY path
ORDER BY pageviews DESC
LIMIT 10;

-- 4. Top referrers (excluding direct traffic) -------------------------------------
SELECT
    referrer_domain,
    count() AS pageviews
FROM events
WHERE site_id = 1
  AND event_type = 'pageview'
  AND referrer_domain != ''
  AND timestamp >= now() - INTERVAL 30 DAY
GROUP BY referrer_domain
ORDER BY pageviews DESC
LIMIT 10;

-- 5. Device breakdown --------------------------------------------------------------
SELECT
    device_type,
    count() AS pageviews
FROM events
WHERE site_id = 1
  AND event_type = 'pageview'
  AND timestamp >= now() - INTERVAL 30 DAY
GROUP BY device_type
ORDER BY pageviews DESC;

-- 6. Standard (auto-tracked) events: insert a couple and check breakdown by name --
INSERT INTO events
    (site_id, event_type, event_name, timestamp, session_id, visitor_id, url, path,
     hostname, properties)
VALUES
    (1, 'standard', 'scroll', now() - INTERVAL 1 DAY, generateUUIDv4(), 'visitor-b',
     'https://example.com/blog/hello-world', '/blog/hello-world', 'example.com',
     map('depth', '90')),
    (1, 'standard', 'outbound_click', now(), generateUUIDv4(), 'visitor-c',
     'https://example.com/pricing', '/pricing', 'example.com',
     map('url', 'https://partner.example.com'));

SELECT
    event_name,
    count() AS occurrences
FROM events
WHERE site_id = 1
  AND event_type = 'standard'
GROUP BY event_name
ORDER BY occurrences DESC;

-- 7. Sanity check on partitioning/ordering: confirm parts land in the expected
--    monthly partitions and EXPLAIN shows the primary key being used for the
--    site_id + time range filter (look for "Selected marks" being small relative
--    to total marks in the table, not a full scan).
EXPLAIN indexes = 1
SELECT count()
FROM events
WHERE site_id = 1
  AND timestamp >= now() - INTERVAL 7 DAY;
