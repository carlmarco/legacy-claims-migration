-- Engine bookkeeping, shared by every dataset. Idempotent.
CREATE SCHEMA IF NOT EXISTS etl;

CREATE TABLE IF NOT EXISTS etl.migration_run (
    run_id       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    dataset      text        NOT NULL,
    strategy     text        NOT NULL,
    dry_run      boolean     NOT NULL DEFAULT false,
    started_at   timestamptz NOT NULL DEFAULT now(),
    finished_at  timestamptz,
    status       text        NOT NULL DEFAULT 'running' CHECK (status IN ('running', 'succeeded', 'failed')),
    stats        jsonb
);

-- Rows that could not be migrated, with the raw legacy row preserved for replay.
CREATE TABLE IF NOT EXISTS etl.quarantine (
    dataset      text   NOT NULL,
    entity       text   NOT NULL,
    src_table    text   NOT NULL,
    src_rec_id   bigint NOT NULL,
    reason       text   NOT NULL,   -- BAD_VALUE | ORPHAN_FK | ...
    detail       text,
    raw          jsonb,
    run_id       bigint REFERENCES etl.migration_run,
    PRIMARY KEY (dataset, entity, src_table, src_rec_id, reason)
);
