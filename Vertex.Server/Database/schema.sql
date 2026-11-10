CREATE SCHEMA branches;
CREATE TABLE branches.branches
(
    id         UUID PRIMARY KEY,
    created_at TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
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

CREATE INDEX idx_branches_owner_id ON branches.branches (owner_id);
CREATE INDEX idx_branches_parent_id ON branches.branches (parent_id);


CREATE SCHEMA projects;
CREATE TABLE projects.projects
(
    id         UUID PRIMARY KEY,
    created_at TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    name       VARCHAR(255) NOT NULL,
    permission INTEGER      NOT NULL DEFAULT 0,
    owner_id   UUID         NOT NULL,

    CONSTRAINT uq_projects_name UNIQUE (name)
);

CREATE INDEX idx_projects_owner_id ON projects.projects (owner_id);


CREATE SCHEMA diagnostics;
CREATE TABLE diagnostics.reports
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

CREATE INDEX idx_request_logs_received_at ON diagnostics.reports (received_at DESC);
CREATE INDEX idx_request_logs_uuid_received_at ON diagnostics.reports (uuid, received_at DESC);
CREATE INDEX idx_request_logs_endpoint ON diagnostics.reports (endpoint);