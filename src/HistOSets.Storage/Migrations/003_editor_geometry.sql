-- Geometry is converted by RegionGeometryCodec before the old tables are removed.
ALTER TABLE catalog_metadata RENAME TO catalog_metadata_v2;
CREATE TABLE catalog_metadata (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1), catalog_id TEXT NOT NULL,
    format_version INTEGER NOT NULL CHECK (format_version = 3), created_utc TEXT NOT NULL,
    name TEXT NOT NULL, modified_utc TEXT NOT NULL,
    content_revision INTEGER NOT NULL DEFAULT 0 CHECK (content_revision >= 0)
) STRICT;
INSERT INTO catalog_metadata SELECT singleton,catalog_id,3,created_utc,name,modified_utc,content_revision FROM catalog_metadata_v2;
DROP TABLE catalog_metadata_v2;
CREATE TABLE regions_v3 (
    id TEXT PRIMARY KEY NOT NULL,
    material_id TEXT NOT NULL REFERENCES materials(id),
    material_image_id TEXT NOT NULL,
    element_id TEXT NOT NULL,
    geometry_type TEXT NOT NULL CHECK (geometry_type IN ('polygon','rectangle','ellipse')),
    coordinate_space TEXT NOT NULL CHECK (coordinate_space = 'pixels'),
    geometry_json TEXT NOT NULL CHECK (json_valid(geometry_json)),
    sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
    revision INTEGER NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_utc TEXT NOT NULL, modified_utc TEXT NOT NULL, deleted_utc TEXT,
    origin_catalog_id TEXT NOT NULL,
    source_batch_id TEXT REFERENCES import_batches(id),
    FOREIGN KEY (element_id,material_id) REFERENCES elements(id,material_id),
    FOREIGN KEY (material_image_id,material_id) REFERENCES material_images(id,material_id)
) STRICT;
