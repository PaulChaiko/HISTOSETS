-- Schema 6 adds independent, localized learning descriptions without changing atlas content.
ALTER TABLE catalog_metadata RENAME TO catalog_metadata_v5;
CREATE TABLE catalog_metadata (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1), catalog_id TEXT NOT NULL,
    format_version INTEGER NOT NULL CHECK (format_version = 6), created_utc TEXT NOT NULL,
    name TEXT NOT NULL, modified_utc TEXT NOT NULL,
    content_revision INTEGER NOT NULL DEFAULT 0 CHECK (content_revision >= 0)
) STRICT;
INSERT INTO catalog_metadata SELECT singleton,catalog_id,6,created_utc,name,modified_utc,content_revision FROM catalog_metadata_v5;
DROP TABLE catalog_metadata_v5;

CREATE TABLE training_prompts (
    id TEXT PRIMARY KEY NOT NULL,
    material_id TEXT NOT NULL REFERENCES materials(id),
    element_id TEXT,
    sort_order INTEGER NOT NULL DEFAULT 0 CHECK (sort_order >= 0),
    is_enabled INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0,1)),
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id),
    FOREIGN KEY (element_id,material_id) REFERENCES elements(id,material_id)
) STRICT;
CREATE INDEX ix_training_prompts_owner ON training_prompts(material_id,element_id,sort_order);

CREATE TABLE training_prompt_translations (
    id TEXT PRIMARY KEY NOT NULL,
    prompt_id TEXT NOT NULL REFERENCES training_prompts(id),
    locale TEXT NOT NULL CHECK (length(trim(locale)) > 0),
    text TEXT NOT NULL,
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id),
    UNIQUE (prompt_id,locale)
) STRICT;
