-- SQLite schema 1. PostgreSQL uses the same entities, keys and relationships;
-- see docs/data-model.md for physical type mappings.
CREATE TABLE schema_migrations (
    version INTEGER PRIMARY KEY,
    applied_utc TEXT NOT NULL
) STRICT;
CREATE TABLE catalog_metadata (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    catalog_id TEXT NOT NULL,
    format_version INTEGER NOT NULL CHECK (format_version = 1),
    created_utc TEXT NOT NULL
) STRICT;
CREATE TABLE import_batches (
    id TEXT PRIMARY KEY NOT NULL,
    source_name TEXT NOT NULL,
    xml_sha256 TEXT NOT NULL CHECK (length(xml_sha256) = 64),
    dataset_sha256 TEXT NOT NULL UNIQUE CHECK (length(dataset_sha256) = 64),
    source_key TEXT NOT NULL UNIQUE,
    mapping_kind TEXT NOT NULL,
    imported_utc TEXT NOT NULL
) STRICT;

CREATE TABLE materials (
    id TEXT PRIMARY KEY NOT NULL,
    kind TEXT NOT NULL DEFAULT 'image',
    author_credit TEXT NOT NULL DEFAULT '',
    rights_credit TEXT NOT NULL DEFAULT '',
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id)
) STRICT;

CREATE TABLE material_translations (
    id TEXT PRIMARY KEY NOT NULL,
    material_id TEXT NOT NULL REFERENCES materials(id),
    locale TEXT NOT NULL,
    name TEXT NOT NULL,
    summary TEXT NOT NULL,
    description TEXT NOT NULL,
    sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id),
    UNIQUE (material_id, locale)
) STRICT;

CREATE TABLE images (
    id TEXT PRIMARY KEY NOT NULL,
    storage_key TEXT NOT NULL UNIQUE,
    sha256 TEXT NOT NULL UNIQUE CHECK (length(sha256) = 64),
    byte_size INTEGER NOT NULL CHECK (byte_size > 0),
    pixel_width INTEGER NOT NULL CHECK (pixel_width > 0),
    pixel_height INTEGER NOT NULL CHECK (pixel_height > 0),
    dpi_x REAL NOT NULL CHECK (dpi_x > 0),
    dpi_y REAL NOT NULL CHECK (dpi_y > 0),
    mime_type TEXT NOT NULL,
    orientation INTEGER NOT NULL CHECK (orientation = 1),
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id)
) STRICT;

CREATE TABLE material_images (
    id TEXT PRIMARY KEY NOT NULL,
    material_id TEXT NOT NULL REFERENCES materials(id),
    image_id TEXT NOT NULL REFERENCES images(id),
    sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id),
    UNIQUE (material_id, image_id),
    UNIQUE (id, material_id)
) STRICT;

CREATE TABLE elements (
    id TEXT PRIMARY KEY NOT NULL,
    material_id TEXT NOT NULL REFERENCES materials(id),
    sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id),
    UNIQUE (id, material_id)
) STRICT;

CREATE TABLE element_translations (
    id TEXT PRIMARY KEY NOT NULL,
    element_id TEXT NOT NULL REFERENCES elements(id),
    locale TEXT NOT NULL,
    name TEXT NOT NULL,
    summary TEXT NOT NULL,
    description TEXT NOT NULL,
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id),
    UNIQUE (element_id, locale)
) STRICT;

CREATE TABLE regions (
    id TEXT PRIMARY KEY NOT NULL,
    material_id TEXT NOT NULL REFERENCES materials(id),
    material_image_id TEXT NOT NULL,
    element_id TEXT NOT NULL,
    geometry_type TEXT NOT NULL CHECK (geometry_type = 'polygon'),
    coordinate_space TEXT NOT NULL CHECK (coordinate_space = 'pixels'),
    sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id),
    FOREIGN KEY (element_id, material_id) REFERENCES elements(id, material_id),
    FOREIGN KEY (material_image_id, material_id) REFERENCES material_images(id, material_id)
) STRICT;

CREATE TABLE region_points (
    region_id TEXT NOT NULL REFERENCES regions(id),
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    x REAL NOT NULL CHECK (x BETWEEN -1e12 AND 1e12),
    y REAL NOT NULL CHECK (y BETWEEN -1e12 AND 1e12),
    PRIMARY KEY (region_id, ordinal)
) STRICT;

CREATE TABLE tag_groups (
    id TEXT PRIMARY KEY NOT NULL,
    code TEXT NOT NULL UNIQUE,
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id)
) STRICT;

CREATE TABLE tag_group_translations (
    id TEXT PRIMARY KEY NOT NULL,
    group_id TEXT NOT NULL REFERENCES tag_groups(id),
    locale TEXT NOT NULL,
    name TEXT NOT NULL,
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id),
    UNIQUE (group_id, locale)
) STRICT;

CREATE TABLE tags (
    id TEXT PRIMARY KEY NOT NULL,
    group_id TEXT REFERENCES tag_groups(id),
    code TEXT NOT NULL UNIQUE,
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id)
) STRICT;

CREATE TABLE tag_translations (
    id TEXT PRIMARY KEY NOT NULL,
    tag_id TEXT NOT NULL REFERENCES tags(id),
    locale TEXT NOT NULL,
    name TEXT NOT NULL,
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id),
    UNIQUE (tag_id, locale)
) STRICT;

CREATE TABLE material_tags (
    id TEXT PRIMARY KEY NOT NULL,
    material_id TEXT NOT NULL REFERENCES materials(id),
    tag_id TEXT NOT NULL REFERENCES tags(id),
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id),
    UNIQUE (material_id, tag_id)
) STRICT;

CREATE INDEX ix_material_translations_order ON material_translations(sort_order, id);
CREATE INDEX ix_material_images_material ON material_images(material_id, sort_order);
CREATE INDEX ix_elements_material ON elements(material_id, sort_order);
CREATE INDEX ix_regions_element ON regions(element_id, sort_order);
CREATE INDEX ix_regions_image ON regions(material_image_id);
CREATE INDEX ix_material_tags_tag ON material_tags(tag_id);
