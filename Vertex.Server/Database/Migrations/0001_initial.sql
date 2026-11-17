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


-- Requests used to be logged here, one row per request. Server metrics now come from OpenTelemetry (VX_METRICS_PORT).
DROP TABLE IF EXISTS diagnostics.requests;
DROP TABLE IF EXISTS diagnostics.reports;
DROP SCHEMA IF EXISTS diagnostics;


-- Revisions: the project's history. Until the first release, schema changes are appended here idempotently instead
-- of in new migration files; existing databases apply this file again by hand (see CONTRIBUTING.md).
--
-- Per-project counters, and the row every write to a project locks. revision_counter numbers revisions (#N) and
-- event_seq numbers events (events.events). A submit or lock change locks this row first, so writes to one project
-- run one at a time: checks against the branch head can't race, and event seqs have no gaps and commit in order.
-- They live in their own narrow table, not on projects.projects, because the row is rewritten on every write: kept
-- small with free space on its page, each update stays a cheap in-place (HOT) update and vacuum keeps up, and
-- foreign-key checks against projects.projects never wait on it.
CREATE TABLE IF NOT EXISTS projects.counters
(
    project_id       UUID   PRIMARY KEY,
    revision_counter BIGINT NOT NULL DEFAULT 0,
    event_seq        BIGINT NOT NULL DEFAULT 0,

    CONSTRAINT fk_counters_project FOREIGN KEY (project_id) REFERENCES projects.projects (id) ON DELETE CASCADE
) WITH (fillfactor = 50, autovacuum_vacuum_scale_factor = 0, autovacuum_vacuum_threshold = 1000);

-- Upgrades databases that kept the counters (and lock_seq before them) on projects.projects.
INSERT INTO projects.counters (project_id) SELECT id FROM projects.projects ON CONFLICT (project_id) DO NOTHING;
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'projects' AND table_name = 'projects' AND column_name = 'revision_counter') THEN
        EXECUTE 'UPDATE projects.counters c SET revision_counter = GREATEST(c.revision_counter, p.revision_counter) FROM projects.projects p WHERE p.id = c.project_id';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'projects' AND table_name = 'projects' AND column_name = 'event_seq') THEN
        EXECUTE 'UPDATE projects.counters c SET event_seq = GREATEST(c.event_seq, p.event_seq) FROM projects.projects p WHERE p.id = c.project_id';
    END IF;
END $$;
ALTER TABLE projects.projects DROP COLUMN IF EXISTS revision_counter;
ALTER TABLE projects.projects DROP COLUMN IF EXISTS event_seq;
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
-- describes, so an event exists exactly when the change committed. seq comes from projects.counters, bumped in that
-- transaction (which locks the project's counters row), so seqs have no gaps and commit in order: a reader resuming after seq N
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

-- The content index: which chunks and file manifests are stored. The bytes stay in S3 (chunks/ in the bucket); these
-- rows answer "is it already uploaded?" for a whole batch in one query, and record a batch of uploads in one commit,
-- instead of one S3 call (and one durable object write) per id. Everything here is immutable and content-addressed,
-- so rows are only ever inserted, and inserting one twice is a no-op. Manifests written before this table existed are
-- still read from S3 (manifests/ in the bucket).
CREATE SCHEMA IF NOT EXISTS files;
CREATE TABLE IF NOT EXISTS files.chunks
(
    -- SHA-256 of the raw chunk bytes.
    id   BYTEA   PRIMARY KEY,
    -- Raw (uncompressed) size in bytes.
    size INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS files.manifests
(
    -- Lowercase hex file id (the hash over the chunk list), as revisions refer to it.
    file_id  TEXT   PRIMARY KEY,
    size     BIGINT NOT NULL,
    -- The FileManifest protobuf message (Files.proto).
    manifest BYTEA  NOT NULL
);
