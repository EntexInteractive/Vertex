-- Initial Vertex schema. The server applies migrations at startup (Database/DatabaseMigrator.cs).
--
-- This one stays idempotent (IF NOT EXISTS) because it also runs against databases created before migrations existed,
-- when this file was applied by hand as schema.sql; it adopts them without touching their data. Later migrations run
-- exactly once and don't need to be idempotent.
--
-- Tables are created in dependency order (tokens and group members reference users, branches and access reference
-- projects).

-- Users, their login tokens, and groups. User and group names share one namespace (enforced by the server), so a
-- project grant can name either without saying which.
--
-- Users are never deleted, only disabled (or, for an erasure request, anonymized in place), because history refers to
-- them. References to auth.users from history and ownership use ON DELETE RESTRICT; only per-login data (tokens,
-- group membership) cascades.
CREATE SCHEMA IF NOT EXISTS auth;
CREATE TABLE IF NOT EXISTS auth.users
(
    id            UUID PRIMARY KEY,
    created_at    BIGINT       NOT NULL,
    username      VARCHAR(64)  NOT NULL,
    display_name  VARCHAR(255) NULL,
    email         VARCHAR(320) NULL,
    -- NULL for accounts that can't log in with a password (e.g. OIDC users, later).
    password_hash TEXT         NULL,
    is_admin      BOOLEAN      NOT NULL DEFAULT FALSE,
    disabled      BOOLEAN      NOT NULL DEFAULT FALSE,
    last_login_at BIGINT       NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_users_username ON auth.users (lower(username));

-- Login tokens. Only the SHA-256 of a token is stored. expires_at slides forward while the token is used.
CREATE TABLE IF NOT EXISTS auth.tokens
(
    id           UUID PRIMARY KEY,
    created_at   BIGINT       NOT NULL,
    user_id      UUID         NOT NULL,
    token_hash   BYTEA        NOT NULL,
    name         VARCHAR(255) NOT NULL,
    last_used_at BIGINT       NOT NULL,
    expires_at   BIGINT       NOT NULL,

    CONSTRAINT uq_tokens_hash UNIQUE (token_hash),
    CONSTRAINT fk_tokens_user FOREIGN KEY (user_id) REFERENCES auth.users (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_tokens_user_id ON auth.tokens (user_id);

CREATE TABLE IF NOT EXISTS auth.groups
(
    id         UUID PRIMARY KEY,
    created_at BIGINT      NOT NULL,
    name       VARCHAR(64) NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_groups_name ON auth.groups (lower(name));

CREATE TABLE IF NOT EXISTS auth.group_members
(
    group_id UUID NOT NULL,
    user_id  UUID NOT NULL,

    CONSTRAINT pk_group_members PRIMARY KEY (group_id, user_id),
    CONSTRAINT fk_group_members_group FOREIGN KEY (group_id) REFERENCES auth.groups (id) ON DELETE CASCADE,
    CONSTRAINT fk_group_members_user FOREIGN KEY (user_id) REFERENCES auth.users (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_group_members_user_id ON auth.group_members (user_id);


CREATE SCHEMA IF NOT EXISTS projects;
CREATE TABLE IF NOT EXISTS projects.projects
(
    id         UUID         PRIMARY KEY,
    created_at BIGINT       NOT NULL,
    name       VARCHAR(255) NOT NULL,
    -- 0 = private (grants only), 1 = internal (every logged-in user can read).
    visibility SMALLINT     NOT NULL DEFAULT 1,
    owner_id   UUID         NOT NULL,

    CONSTRAINT uq_projects_name UNIQUE (name),
    CONSTRAINT fk_projects_owner FOREIGN KEY (owner_id) REFERENCES auth.users (id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS idx_projects_owner_id ON projects.projects (owner_id);

-- Upgrades databases created before visibility replaced the unused permission column.
ALTER TABLE projects.projects ADD COLUMN IF NOT EXISTS visibility SMALLINT NOT NULL DEFAULT 1;
ALTER TABLE projects.projects DROP COLUMN IF EXISTS permission;

-- Project access grants. principal_id is a user (kind 0) or a group (kind 1); it has no foreign key because it can
-- point at either table, so the server deletes a principal's grants when it deletes the user or group.
-- level: 1 = read, 2 = write, 3 = admin.
CREATE TABLE IF NOT EXISTS projects.access
(
    project_id     UUID     NOT NULL,
    principal_id   UUID     NOT NULL,
    principal_kind SMALLINT NOT NULL,
    level          SMALLINT NOT NULL,

    CONSTRAINT pk_access PRIMARY KEY (project_id, principal_id),
    CONSTRAINT fk_access_project FOREIGN KEY (project_id) REFERENCES projects.projects (id) ON DELETE CASCADE,
    CONSTRAINT chk_access_kind CHECK (principal_kind IN (0, 1)),
    CONSTRAINT chk_access_level CHECK (level BETWEEN 1 AND 3)
);

CREATE INDEX IF NOT EXISTS idx_access_principal_id ON projects.access (principal_id);


CREATE SCHEMA IF NOT EXISTS branches;
CREATE TABLE IF NOT EXISTS branches.branches
(
    id         UUID         PRIMARY KEY,
    created_at BIGINT       NOT NULL,
    name       VARCHAR(255) NOT NULL,
    project_id UUID         NOT NULL,
    parent_id  UUID         NULL,

    CONSTRAINT uq_branches_project_name UNIQUE (project_id, name),
    CONSTRAINT uq_branches_id_project UNIQUE (id, project_id),
    CONSTRAINT fk_branches_project FOREIGN KEY (project_id) REFERENCES projects.projects (id) ON DELETE CASCADE,
    CONSTRAINT fk_branches_parent FOREIGN KEY (parent_id, project_id) REFERENCES branches.branches (id, project_id) ON DELETE NO ACTION,
    CONSTRAINT chk_branches_not_self_parent CHECK (parent_id IS NULL OR parent_id <> id)
);

CREATE INDEX IF NOT EXISTS idx_branches_parent_id ON branches.branches (parent_id);


CREATE SCHEMA IF NOT EXISTS diagnostics;
CREATE TABLE IF NOT EXISTS diagnostics.requests
(
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    received_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    auth_user   UUID        NULL,
    method      VARCHAR(10) NOT NULL,
    endpoint    TEXT        NOT NULL,
    status_code INTEGER     NOT NULL,
    process_ms  BIGINT      NOT NULL,
    length      BIGINT      NOT NULL,

    CONSTRAINT chk_request_logs_status_code CHECK (status_code BETWEEN 100 AND 599),
    CONSTRAINT chk_request_logs_process_ms CHECK (process_ms >= 0),
    CONSTRAINT chk_request_logs_length CHECK (length >= 0)
);

CREATE INDEX IF NOT EXISTS idx_request_logs_received_at ON diagnostics.requests (received_at DESC);
CREATE INDEX IF NOT EXISTS idx_request_logs_auth_user_received_at ON diagnostics.requests (auth_user, received_at DESC);
CREATE INDEX IF NOT EXISTS idx_request_logs_endpoint ON diagnostics.requests (endpoint);


-- Revisions: the project's history. Until the first release, schema changes are appended here idempotently instead
-- of in new migration files; existing databases apply this file again by hand (see CONTRIBUTING.md).
--
-- Revision numbers (#N) are per project and come from projects.revision_counter, which a submit increments first:
-- that row lock serializes submits within a project, so checks against the branch head can't race.
ALTER TABLE projects.projects ADD COLUMN IF NOT EXISTS revision_counter BIGINT NOT NULL DEFAULT 0;
-- The project's event sequence (events.events); bumping it also locks the project row. lock_seq was its lock-only
-- predecessor.
ALTER TABLE projects.projects ADD COLUMN IF NOT EXISTS event_seq BIGINT NOT NULL DEFAULT 0;
ALTER TABLE projects.projects DROP COLUMN IF EXISTS lock_seq;

CREATE SCHEMA IF NOT EXISTS revisions;
CREATE TABLE IF NOT EXISTS revisions.revisions
(
    id         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id UUID   NOT NULL,
    branch_id  UUID   NOT NULL,
    number     BIGINT NOT NULL,
    user_id    UUID   NOT NULL,
    message    TEXT   NOT NULL,
    created_at BIGINT NOT NULL,

    CONSTRAINT uq_revisions_project_number UNIQUE (project_id, number),
    CONSTRAINT fk_revisions_branch FOREIGN KEY (branch_id, project_id) REFERENCES branches.branches (id, project_id) ON DELETE CASCADE,
    CONSTRAINT fk_revisions_user FOREIGN KEY (user_id) REFERENCES auth.users (id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS idx_revisions_branch_number ON revisions.revisions (branch_id, number DESC);

-- What each revision changed. action: 0 = created, 1 = modified, 2 = deleted. file_id is NULL for deletions.
-- A rename is stored as a deletion plus a creation.
CREATE TABLE IF NOT EXISTS revisions.changes
(
    revision_id BIGINT   NOT NULL,
    path        TEXT     NOT NULL,
    action      SMALLINT NOT NULL,
    file_id     TEXT     NULL,

    CONSTRAINT pk_changes PRIMARY KEY (revision_id, path),
    CONSTRAINT fk_changes_revision FOREIGN KEY (revision_id) REFERENCES revisions.revisions (id) ON DELETE CASCADE,
    CONSTRAINT chk_changes_action CHECK (action BETWEEN 0 AND 2),
    CONSTRAINT chk_changes_file_id CHECK ((action = 2) = (file_id IS NULL))
);

-- The current content of every file on each branch, so sync and submit never replay history. path_key is
-- lower(path): game projects live on case-insensitive file systems, so two paths differing only in case are one file.
-- revision is the number of the revision that last changed the file, which sync uses to fetch only what's new.
CREATE TABLE IF NOT EXISTS revisions.head
(
    branch_id UUID   NOT NULL,
    path_key  TEXT   NOT NULL,
    path      TEXT   NOT NULL,
    file_id   TEXT   NOT NULL,
    revision  BIGINT NOT NULL,

    CONSTRAINT pk_head PRIMARY KEY (branch_id, path_key),
    CONSTRAINT fk_head_branch FOREIGN KEY (branch_id) REFERENCES branches.branches (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_head_branch_revision ON revisions.head (branch_id, revision);


-- File locks. A file is locked on a branch by one user in one workspace from the moment their client sees it change
-- until it is submitted, reverted or changed back. Other clients make locked files read-only, and submit refuses
-- files locked by someone else. path_key is lower(path), like revisions.head.
CREATE SCHEMA IF NOT EXISTS locks;
CREATE TABLE IF NOT EXISTS locks.locks
(
    branch_id    UUID   NOT NULL,
    path_key     TEXT   NOT NULL,
    path         TEXT   NOT NULL,
    user_id      UUID   NOT NULL,
    workspace_id UUID   NOT NULL,
    -- The machine the workspace is on, so "locked by alice on DESKTOP-7" tells people where to look.
    host         TEXT   NOT NULL,
    created_at   BIGINT NOT NULL,

    CONSTRAINT pk_locks PRIMARY KEY (branch_id, path_key),
    CONSTRAINT fk_locks_branch FOREIGN KEY (branch_id) REFERENCES branches.branches (id) ON DELETE CASCADE,
    CONSTRAINT fk_locks_user FOREIGN KEY (user_id) REFERENCES auth.users (id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS idx_locks_workspace ON locks.locks (workspace_id);

-- The lock-only journal was replaced by events.events.
DROP TABLE IF EXISTS locks.events;


-- Project events: everything that happens in a project (files locked, revisions submitted, later builds), so clients,
-- build triggers and plugins can follow along. Each event is written in the same transaction as the change it
-- describes, so an event exists exactly when the change committed. seq comes from projects.event_seq, bumped in that
-- transaction (which locks the project row), so seqs have no gaps and commit in order: a reader resuming after seq N
-- can never skip one. One event per operation: locking 10,000 files is one event listing them. payload holds the
-- details as JSON, e.g. {"paths": [...], "reason": "submitted"} or {"revision": 42, "message": "...", "files": 3}.
-- Old events are pruned; a reader whose cursor is older reloads the state it needs instead.
CREATE SCHEMA IF NOT EXISTS events;
CREATE TABLE IF NOT EXISTS events.events
(
    project_id   UUID   NOT NULL,
    seq          BIGINT NOT NULL,
    type         TEXT   NOT NULL,
    branch_id    UUID   NULL,
    user_id      UUID   NOT NULL,
    workspace_id UUID   NULL,
    host         TEXT   NULL,
    payload      JSONB  NOT NULL,
    created_at   BIGINT NOT NULL,

    CONSTRAINT pk_events PRIMARY KEY (project_id, seq),
    CONSTRAINT fk_events_project FOREIGN KEY (project_id) REFERENCES projects.projects (id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_events_created_at ON events.events (created_at);

-- How far each server-side event handler (build triggers, plugins) got in each project, so every event is handled
-- even across restarts and by one instance at a time.
CREATE TABLE IF NOT EXISTS events.cursors
(
    handler    TEXT   NOT NULL,
    project_id UUID   NOT NULL,
    seq        BIGINT NOT NULL,

    CONSTRAINT pk_event_cursors PRIMARY KEY (handler, project_id),
    CONSTRAINT fk_event_cursors_project FOREIGN KEY (project_id) REFERENCES projects.projects (id) ON DELETE CASCADE
);
