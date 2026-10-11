-- Schema 5 records acknowledged source/local content, independently of revision counters.
ALTER TABLE catalog_metadata RENAME TO catalog_metadata_v4;
CREATE TABLE catalog_metadata (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1), catalog_id TEXT NOT NULL,
    format_version INTEGER NOT NULL CHECK (format_version = 5), created_utc TEXT NOT NULL,
    name TEXT NOT NULL, modified_utc TEXT NOT NULL,
    content_revision INTEGER NOT NULL DEFAULT 0 CHECK (content_revision >= 0)
) STRICT;
INSERT INTO catalog_metadata SELECT singleton,catalog_id,5,created_utc,name,modified_utc,content_revision FROM catalog_metadata_v4;
DROP TABLE catalog_metadata_v4;
CREATE TABLE exchange_baselines (
    source_catalog_id TEXT NOT NULL,
    entity_kind TEXT NOT NULL CHECK (entity_kind IN ('material','tag','tag_group','analog_group')),
    entity_id TEXT NOT NULL,
    source_fingerprint TEXT NOT NULL CHECK (length(source_fingerprint) = 64),
    local_fingerprint TEXT NOT NULL CHECK (length(local_fingerprint) = 64),
    source_revision INTEGER NOT NULL CHECK (source_revision > 0),
    acknowledged_utc TEXT NOT NULL,
    PRIMARY KEY(source_catalog_id,entity_kind,entity_id)
) STRICT;
