-- Schema 4 extends the allowed vector geometry without rewriting existing JSON.
ALTER TABLE catalog_metadata RENAME TO catalog_metadata_v3;
CREATE TABLE catalog_metadata (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1), catalog_id TEXT NOT NULL,
    format_version INTEGER NOT NULL CHECK (format_version = 4), created_utc TEXT NOT NULL,
    name TEXT NOT NULL, modified_utc TEXT NOT NULL,
    content_revision INTEGER NOT NULL DEFAULT 0 CHECK (content_revision >= 0)
) STRICT;
INSERT INTO catalog_metadata SELECT singleton,catalog_id,4,created_utc,name,modified_utc,content_revision FROM catalog_metadata_v3;
DROP TABLE catalog_metadata_v3;
CREATE TABLE regions_v4 (
    id TEXT PRIMARY KEY NOT NULL,
    material_id TEXT NOT NULL REFERENCES materials(id),
    material_image_id TEXT NOT NULL,
    element_id TEXT NOT NULL,
    geometry_type TEXT NOT NULL CHECK (geometry_type IN ('polygon','rectangle','ellipse','compound')),
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
INSERT INTO regions_v4 SELECT * FROM regions;
DROP TABLE regions;
ALTER TABLE regions_v4 RENAME TO regions;
CREATE INDEX ix_regions_element ON regions(element_id,sort_order);
CREATE INDEX ix_regions_image ON regions(material_image_id);
