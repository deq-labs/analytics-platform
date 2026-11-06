-- App metadata: sites + per-site standard-event tracking config.
-- Pulled forward from Phase 7 (originally full auth/users/api_keys) because the
-- tracking snippet needs somewhere to read per-site config from before it knows
-- which standard/auto-tracked events to instrument (GA "Enhanced Measurement"
-- style toggles -- see CLAUDE.md "Standard (auto-tracked) events"). Users, auth,
-- and API keys are still Phase 7, not built here.

CREATE TABLE IF NOT EXISTS sites (
    id          SERIAL PRIMARY KEY,
    name        TEXT NOT NULL,
    domain      TEXT NOT NULL UNIQUE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- One row per (site, standard event), seeded when a site is created (app logic,
-- not yet built -- Phase 2/7). page_view is deliberately NOT in the CHECK list:
-- it's core tracking, not part of the optional/toggleable standard-event set,
-- same as GA's page_view sitting outside the Enhanced Measurement toggle group.
CREATE TABLE IF NOT EXISTS site_tracked_events (
    site_id     INTEGER NOT NULL REFERENCES sites(id) ON DELETE CASCADE,
    event_key   TEXT NOT NULL CHECK (event_key IN (
                    'scroll', 'outbound_click', 'file_download',
                    'form_start', 'form_submit',
                    'video_start', 'video_progress', 'video_complete'
                )),
    enabled     BOOLEAN NOT NULL DEFAULT true,
    PRIMARY KEY (site_id, event_key)
);
