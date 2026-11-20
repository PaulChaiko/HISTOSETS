-- Schema 7: native SQLite document, original image bytes and optional export provenance.
ALTER TABLE catalog_metadata RENAME TO catalog_metadata_v6;
CREATE TABLE catalog_metadata (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1), catalog_id TEXT NOT NULL,
    format_version INTEGER NOT NULL CHECK (format_version = 7), created_utc TEXT NOT NULL,
    name TEXT NOT NULL, modified_utc TEXT NOT NULL,
    content_revision INTEGER NOT NULL DEFAULT 0 CHECK (content_revision >= 0)
) STRICT;
INSERT INTO catalog_metadata SELECT singleton,catalog_id,7,created_utc,name,modified_utc,content_revision FROM catalog_metadata_v6;
DROP TABLE catalog_metadata_v6;

-- A rowid is intentional: SQLite incremental BLOB I/O addresses rows by rowid.
CREATE TABLE image_contents (
    image_id TEXT NOT NULL UNIQUE REFERENCES images(id),
    data BLOB NOT NULL CHECK (length(data) > 0)
) STRICT;
CREATE TABLE exchange_origin (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    source_catalog_id TEXT NOT NULL, source_catalog_name TEXT NOT NULL,
    source_content_revision INTEGER NOT NULL CHECK (source_content_revision >= 0),
    exported_utc TEXT NOT NULL
) STRICT;
