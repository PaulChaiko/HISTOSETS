-- Schema 2: catalog organization. Existing geometry and scientific content are unchanged.
ALTER TABLE catalog_metadata RENAME TO catalog_metadata_v1;
CREATE TABLE catalog_metadata (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    catalog_id TEXT NOT NULL,
    format_version INTEGER NOT NULL CHECK (format_version = 2),
    created_utc TEXT NOT NULL,
    name TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    content_revision INTEGER NOT NULL DEFAULT 0 CHECK (content_revision >= 0)
) STRICT;
INSERT INTO catalog_metadata
SELECT singleton, catalog_id, 2, created_utc, '', created_utc, 0 FROM catalog_metadata_v1;
DROP TABLE catalog_metadata_v1;

CREATE TABLE analog_groups (
    id TEXT PRIMARY KEY NOT NULL,
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id)
) STRICT;
CREATE TABLE analog_group_translations (
    id TEXT PRIMARY KEY NOT NULL,
    group_id TEXT NOT NULL REFERENCES analog_groups(id),
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
ALTER TABLE materials ADD COLUMN status TEXT NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'ready'));
ALTER TABLE materials ADD COLUMN analog_group_id TEXT REFERENCES analog_groups(id);
ALTER TABLE material_images ADD COLUMN is_primary INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0, 1));
CREATE UNIQUE INDEX ix_one_primary_image ON material_images(material_id)
    WHERE is_primary = 1 AND deleted_utc IS NULL;
CREATE INDEX ix_materials_analog_group ON materials(analog_group_id);

ALTER TABLE tag_groups ADD COLUMN is_system INTEGER NOT NULL DEFAULT 0 CHECK (is_system IN (0, 1));

CREATE TABLE image_calibrations (
    id TEXT PRIMARY KEY NOT NULL,
    material_image_id TEXT NOT NULL REFERENCES material_images(id),
    pixel_size_x_m REAL NOT NULL CHECK (pixel_size_x_m > 0 AND pixel_size_x_m <= 1e12),
    pixel_size_y_m REAL NOT NULL CHECK (pixel_size_y_m > 0 AND pixel_size_y_m <= 1e12),
    source_kind TEXT NOT NULL CHECK (source_kind IN ('manual_dimensions', 'image_metadata', 'scale_bar')),
    source_note TEXT NOT NULL DEFAULT '',
    input_width REAL,
    input_height REAL,
    input_unit TEXT,
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id),
    CHECK ((input_width IS NULL AND input_height IS NULL AND input_unit IS NULL)
      OR (input_width IS NOT NULL AND input_height IS NOT NULL AND input_unit IS NOT NULL
          AND input_width > 0 AND input_height > 0 AND input_unit IN ('m','cm','mm','um','nm')))
) STRICT;
CREATE UNIQUE INDEX ix_one_active_calibration ON image_calibrations(material_image_id) WHERE deleted_utc IS NULL;
