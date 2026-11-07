-- Secret API keys (Phase 7 step 4, 2026-11-07) for reading stats from a
-- server: `Authorization: Bearer apk_...`. A key acts as its user, read-only,
-- and never with admin powers.
--
-- Unlike the public tracking ID, a key IS a secret: it's shown once when
-- created and only its SHA-256 hash is stored (a fast hash is fine here because
-- keys are 256 random bits -- nothing to brute-force, unlike passwords).
-- `prefix` (the first characters) is kept so people can tell keys apart.
--
-- Safe to run more than once, and on databases created before this file.

CREATE TABLE IF NOT EXISTS api_keys (
    id            SERIAL PRIMARY KEY,
    user_id       INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name          TEXT NOT NULL,
    prefix        TEXT NOT NULL,
    key_hash      BYTEA NOT NULL UNIQUE,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_used_at  TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS api_keys_user_id_idx ON api_keys (user_id);
