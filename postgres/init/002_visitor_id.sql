-- Visitor identification (decided 2026-11-06). Each site chooses how visitors
-- are identified -- see CLAUDE.md "Visitor identification" for the full design.
--
--   'hash'       (default) cookie-less: the server derives visitor_id from
--                SHA-256(daily salt + site + IP + user agent). Nothing is stored
--                on the visitor's device; the same person on two different days
--                counts as two visitors.
--   'persistent' the tracking snippet keeps a random UUID in localStorage and
--                sends it with each event. Accurate returning visitors, but the
--                site owner is responsible for getting consent where required.
--                If no valid ID arrives (e.g. consent not given yet), ingestion
--                falls back to the hash.
--
-- Written to be safe to run more than once, so it can also be applied by hand to
-- a database created before this file existed.

ALTER TABLE sites
    ADD COLUMN IF NOT EXISTS visitor_id_mode TEXT NOT NULL DEFAULT 'hash';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'sites_visitor_id_mode_check'
    ) THEN
        ALTER TABLE sites
            ADD CONSTRAINT sites_visitor_id_mode_check
            CHECK (visitor_id_mode IN ('hash', 'persistent'));
    END IF;
END $$;

-- One random salt per UTC day for cookie-less hashing (also used for ip_hash).
-- Old salts are deleted by the ingestion API, which is what makes yesterday's
-- hashes impossible to link to today's: once the salt is gone, nobody -- including
-- us -- can recompute them. Kept in Postgres rather than in memory so restarting
-- the API mid-day doesn't change every visitor's ID.
CREATE TABLE IF NOT EXISTS daily_salts (
    day   DATE PRIMARY KEY,
    salt  BYTEA NOT NULL
);
