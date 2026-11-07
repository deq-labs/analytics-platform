-- Public tracking IDs (Phase 7, decided 2026-11-07). The tracking snippet
-- identifies its site with this instead of the sequential numeric id, which
-- anyone could enumerate (1, 2, 3, ...). Like GA's measurement ID it is
-- public -- it appears in page source -- so it identifies, it doesn't
-- authenticate. The domain check at ingestion is what keeps other sites from
-- sending events with it.
--
-- Format: "ap_" + 16 hex characters (64 random bits) from gen_random_uuid(),
-- which is built into Postgres 13+ and cryptographically random.
--
-- Safe to run more than once, and on databases created before this file.

ALTER TABLE sites
    ADD COLUMN IF NOT EXISTS tracking_id TEXT NOT NULL
        DEFAULT 'ap_' || substr(replace(gen_random_uuid()::text, '-', ''), 1, 16);

-- ADD COLUMN ... DEFAULT evaluates the volatile default per existing row, so
-- every existing site already got its own ID above.

CREATE UNIQUE INDEX IF NOT EXISTS sites_tracking_id_key ON sites (tracking_id);
