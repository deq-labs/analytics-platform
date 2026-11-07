-- Local development data for Postgres. Safe to run more than once.
--
--   docker exec -i analytics-postgres psql -U analytics -d analytics_app < scripts/dev_seed.sql
--
-- Creates three sites:
--   localhost             visitor_id_mode = 'hash'       (cookie-less)
--   persistent.localhost  visitor_id_mode = 'persistent' (browser-stored ID)
--   demo.localhost        fake traffic from scripts/dev_events.sql
-- Prints their ids at the end -- use those as "siteId" when sending events.

INSERT INTO sites (name, domain, visitor_id_mode) VALUES
    ('Local dev (hash)',       'localhost',            'hash'),
    ('Local dev (persistent)', 'persistent.localhost', 'persistent'),
    ('Demo data',              'demo.localhost',       'hash')  -- filled by scripts/dev_events.sql
ON CONFLICT (domain) DO NOTHING;

-- Every standard event enabled (the default, opt-out like GA).
INSERT INTO site_tracked_events (site_id, event_key)
SELECT s.id, e.key
FROM sites s
CROSS JOIN (VALUES
    ('scroll'), ('outbound_click'), ('file_download'),
    ('form_start'), ('form_submit'),
    ('video_start'), ('video_progress'), ('video_complete')
) AS e(key)
WHERE s.domain IN ('localhost', 'persistent.localhost', 'demo.localhost')
ON CONFLICT DO NOTHING;

SELECT id, name, domain, visitor_id_mode FROM sites ORDER BY id;
