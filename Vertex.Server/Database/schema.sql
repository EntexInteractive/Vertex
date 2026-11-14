-- Vertex database schema. Safe to run more than once: everything is created only if missing.
-- Tables are created in dependency order (tokens and group members reference users, branches and access reference
-- projects).

-- Users, their login tokens, and groups. User and group names share one namespace (enforced by the server), so a
-- project grant can name either without saying which.
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
    id
    UUID
    PRIMARY
    KEY,
    created_at
    BIGINT
    NOT
    NULL,
    name
    VARCHAR
(
    255
) NOT NULL,
    -- 0 = private (grants only), 1 = internal (every logged-in user can read).
    visibility SMALLINT NOT NULL DEFAULT 1,
    owner_id UUID NOT NULL,
    CONSTRAINT uq_projects_name UNIQUE
(
    name
)
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
    id         UUID PRIMARY KEY,
    created_at
    BIGINT
    NOT
    NULL,
    name       VARCHAR(255) NOT NULL,
    project_id UUID         NOT NULL,
    owner_id   UUID         NOT NULL,
    parent_id  UUID NULL,

    CONSTRAINT uq_branches_project_name UNIQUE (project_id, name),
    CONSTRAINT uq_branches_id_project UNIQUE (id, project_id),
    CONSTRAINT fk_branches_project FOREIGN KEY (project_id) REFERENCES projects.projects (id) ON DELETE CASCADE,
    CONSTRAINT fk_branches_parent FOREIGN KEY (parent_id, project_id) REFERENCES branches.branches (id, project_id) ON DELETE NO ACTION,
    CONSTRAINT chk_branches_not_self_parent CHECK (parent_id IS NULL OR parent_id <> id)
);

CREATE INDEX IF NOT EXISTS idx_branches_owner_id ON branches.branches (owner_id);
CREATE INDEX IF NOT EXISTS idx_branches_parent_id ON branches.branches (parent_id);


CREATE SCHEMA IF NOT EXISTS diagnostics;
-- Databases created before the table was named requests get it renamed in place.
ALTER TABLE IF EXISTS diagnostics.reports RENAME TO requests;
CREATE TABLE IF NOT EXISTS diagnostics.requests
(
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    received_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    uuid        VARCHAR(255) NULL,
    method      VARCHAR(10) NOT NULL,
    endpoint    TEXT        NOT NULL,
    status_code INTEGER     NOT NULL,
    process_ms  BIGINT      NOT NULL,
    length      BIGINT      NOT NULL,
    cpu_usage   INTEGER     NOT NULL,
    ram_usage   BIGINT      NOT NULL,

    CONSTRAINT chk_request_logs_status_code CHECK (status_code BETWEEN 100 AND 599),
    CONSTRAINT chk_request_logs_process_ms CHECK (process_ms >= 0),
    CONSTRAINT chk_request_logs_length CHECK (length >= 0)
);

CREATE INDEX IF NOT EXISTS idx_request_logs_received_at ON diagnostics.requests (received_at DESC);
CREATE INDEX IF NOT EXISTS idx_request_logs_uuid_received_at ON diagnostics.requests (uuid, received_at DESC);
CREATE INDEX IF NOT EXISTS idx_request_logs_endpoint ON diagnostics.requests (endpoint);
