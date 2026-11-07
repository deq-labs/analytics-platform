-- Generates 30 days of fake pageviews for the "Demo data" site, so the query
-- API and dashboard have something realistic to show. Run after dev_seed.sql:
--
--   docker exec -i analytics-clickhouse clickhouse-client --user analytics \
--     --password <your password> --database analytics --param_site=<demo site id> \
--     < scripts/dev_events.sql
--
-- Get the demo site id from the last lines dev_seed.sql prints. Running this
-- twice doubles the data; to start over:
--   ALTER TABLE events DELETE WHERE hostname = 'demo.localhost'
--
-- How it works: numbers(25000) is a built-in table with one row per number
-- 0..24999. Each row becomes one pageview with random values picked by
-- rand(n) -- a different random stream per n, so the columns are independent.

INSERT INTO events
    (site_id, event_type, timestamp, session_id, visitor_id, url, path, hostname,
     referrer, referrer_domain, user_agent, screen_width, screen_height)
SELECT
    {site:UInt32},
    'pageview',
    ts,
    -- Same visitor on the same day = one session. reinterpretAsUUID turns the
    -- 16-byte MD5 into a UUID, so this is deterministic, not random.
    reinterpretAsUUID(MD5(concat(visitor, toString(toDate(ts))))),
    visitor,
    concat('https://demo.localhost', path),
    path,
    'demo.localhost',
    referrer,
    replaceRegexpOne(domain(referrer), '^www\\.', ''),
    'DemoData/1.0',
    1920,
    1080
FROM
(
    SELECT
        -- Spread over the last 30 days. pow(x, 1.4) pushes more events toward
        -- "now", so the chart shows growth instead of a flat line.
        now64(3) - toIntervalSecond(toUInt32(30 * 86400 * pow(rand(1) / 4294967295.0, 1.4))) AS ts,
        concat('h:demo-visitor-', toString(rand(2) % 4000)) AS visitor,
        -- Arrays are 1-indexed in ClickHouse. Repeated entries = more popular.
        ['/', '/', '/', '/pricing', '/pricing', '/docs', '/docs', '/docs/getting-started',
         '/blog/clickhouse-for-analytics', '/blog/csharp-minimal-apis', '/about', '/signup']
            [1 + rand(3) % 12] AS path,
        ['', '', '', '', 'https://www.google.com/', 'https://www.google.com/', 'https://google.com/',
         'https://news.ycombinator.com/', 'https://github.com/deq-labs/analytics-platform',
         'https://www.reddit.com/r/dotnet/', 'https://duckduckgo.com/', 'https://t.co/abc123']
            [1 + rand(4) % 12] AS referrer
    FROM numbers(25000)
);
