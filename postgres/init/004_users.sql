-- Users, site ownership, and invites (Phase 7 step 2, 2026-11-07).
--
-- * The first account created becomes the admin and takes ownership of any
--   sites that have no owner yet (e.g. sites from before this migration).
-- * Registration is invite-only by default (self-hosting); Auth:OpenRegistration
--   = true lets anyone sign up (SaaS).
-- * Passwords are stored as ASP.NET Core Identity hashes (PBKDF2 with a
--   per-user salt), never in plain text.
--
-- Safe to run more than once, and on databases created before this file.

CREATE TABLE IF NOT EXISTS users (
    id             SERIAL PRIMARY KEY,
    email          TEXT NOT NULL,
    password_hash  TEXT NOT NULL,
    is_admin       BOOLEAN NOT NULL DEFAULT false,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Emails are unique regardless of case ("Me@x.com" = "me@x.com").
CREATE UNIQUE INDEX IF NOT EXISTS users_email_key ON users (lower(email));

-- Nullable: sites created before users existed have no owner until the first
-- account claims them. Deleting a user deletes their sites.
ALTER TABLE sites
    ADD COLUMN IF NOT EXISTS owner_user_id INTEGER REFERENCES users(id) ON DELETE CASCADE;

-- One-time invite links. Only a SHA-256 hash of the token is stored, so a
-- leaked database can't be used to sign up.
CREATE TABLE IF NOT EXISTS invites (
    id          SERIAL PRIMARY KEY,
    token_hash  BYTEA NOT NULL UNIQUE,
    created_by  INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at  TIMESTAMPTZ NOT NULL,
    used_by     INTEGER REFERENCES users(id) ON DELETE SET NULL,
    used_at     TIMESTAMPTZ
);
