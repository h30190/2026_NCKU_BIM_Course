package main

import (
	"database/sql"
	"fmt"
	"os"
	"path/filepath"

	_ "modernc.org/sqlite"
)

// OpenDB opens the SQLite database and applies additive migrations.
func OpenDB(dbPath string) (*sql.DB, error) {
	if err := os.MkdirAll(filepath.Dir(dbPath), 0o755); err != nil {
		return nil, err
	}
	db, err := sql.Open("sqlite", dbPath)
	if err != nil {
		return nil, err
	}
	// One local SQLite writer; revision guards still decide which request wins.
	db.SetMaxOpenConns(1)
	if err := migrate(db); err != nil {
		db.Close()
		return nil, err
	}
	return db, nil
}

func migrate(db *sql.DB) error {
	stmts := []string{
		`CREATE TABLE IF NOT EXISTS model_versions (
			id INTEGER PRIMARY KEY AUTOINCREMENT,
			label TEXT NOT NULL DEFAULT '',
			model_name TEXT NOT NULL DEFAULT '',
			created_at DATETIME NOT NULL DEFAULT (datetime('now'))
		)`,
		`CREATE TABLE IF NOT EXISTS element_snapshots (
			id INTEGER PRIMARY KEY AUTOINCREMENT,
			version_id INTEGER NOT NULL,
			unique_id TEXT NOT NULL,
			element_id TEXT NOT NULL DEFAULT '',
			type_name TEXT NOT NULL DEFAULT '',
			material TEXT NOT NULL DEFAULT '',
			quantity REAL NOT NULL DEFAULT 0,
			position_x REAL NOT NULL DEFAULT 0,
			position_y REAL NOT NULL DEFAULT 0,
			position_z REAL NOT NULL DEFAULT 0,
			level_name TEXT NOT NULL DEFAULT '',
			FOREIGN KEY(version_id) REFERENCES model_versions(id)
		)`,
		`CREATE INDEX IF NOT EXISTS idx_snapshots_version ON element_snapshots(version_id)`,
		`CREATE INDEX IF NOT EXISTS idx_snapshots_uniqueid ON element_snapshots(unique_id)`,
		`CREATE TABLE IF NOT EXISTS passports (
			id INTEGER PRIMARY KEY AUTOINCREMENT,
			passport_id TEXT NOT NULL UNIQUE,
			category TEXT NOT NULL DEFAULT '',
			source_model TEXT NOT NULL DEFAULT '',
			source_unique_id TEXT NOT NULL DEFAULT '',
			source_element_id TEXT NOT NULL DEFAULT '',
			family TEXT NOT NULL DEFAULT '',
			type_name TEXT NOT NULL DEFAULT '',
			width_mm REAL NOT NULL DEFAULT 0,
			height_mm REAL NOT NULL DEFAULT 0,
			dimension_definition TEXT NOT NULL DEFAULT '',
			leaf_thickness_mm REAL NOT NULL DEFAULT 0,
			frame_dimensions TEXT NOT NULL DEFAULT '',
			operation_type TEXT NOT NULL DEFAULT '',
			swing_handing TEXT NOT NULL DEFAULT '',
			use_type TEXT NOT NULL DEFAULT '',
			clear_width_mm REAL NOT NULL DEFAULT 0,
			leaf_count INTEGER NOT NULL DEFAULT 1,
			material TEXT NOT NULL DEFAULT '',
			fire_rating TEXT NOT NULL DEFAULT '',
			fire_minutes INTEGER NOT NULL DEFAULT 0,
			fire_certificate_reference TEXT NOT NULL DEFAULT '',
			fire_certificate_verified INTEGER NOT NULL DEFAULT 0,
			condition_text TEXT NOT NULL DEFAULT '',
			condition_grade TEXT NOT NULL DEFAULT '',
			reuse_status TEXT NOT NULL DEFAULT 'Pending',
			review_note TEXT NOT NULL DEFAULT '',
			quantity INTEGER NOT NULL DEFAULT 1,
			price_ntd REAL NOT NULL DEFAULT 0,
			placement_requirements TEXT NOT NULL DEFAULT '',
			inspected_by TEXT NOT NULL DEFAULT '',
			inspected_on TEXT NOT NULL DEFAULT '',
			photo TEXT NOT NULL DEFAULT '',
			status TEXT NOT NULL DEFAULT 'Unavailable',
			target_model TEXT NOT NULL DEFAULT '',
			target_element_id TEXT NOT NULL DEFAULT '',
			revision INTEGER NOT NULL DEFAULT 1,
			raw_parameters_json TEXT NOT NULL DEFAULT '[]',
			field_sources_json TEXT NOT NULL DEFAULT '{}',
			mapping_warnings_json TEXT NOT NULL DEFAULT '[]',
			created_at DATETIME NOT NULL DEFAULT (datetime('now')),
			updated_at DATETIME NOT NULL DEFAULT (datetime('now'))
		)`,
		`CREATE INDEX IF NOT EXISTS idx_passports_status ON passports(status)`,
		`CREATE INDEX IF NOT EXISTS idx_passports_reuse ON passports(reuse_status)`,
		`CREATE INDEX IF NOT EXISTS idx_passports_source ON passports(source_model, source_unique_id)`,
		`CREATE TABLE IF NOT EXISTS passport_events (
			id INTEGER PRIMARY KEY AUTOINCREMENT,
			passport_id INTEGER NOT NULL,
			action TEXT NOT NULL,
			actor TEXT NOT NULL DEFAULT '',
			details_json TEXT NOT NULL DEFAULT '{}',
			created_at DATETIME NOT NULL DEFAULT (datetime('now')),
			FOREIGN KEY(passport_id) REFERENCES passports(id)
		)`,
		`CREATE INDEX IF NOT EXISTS idx_passport_events ON passport_events(passport_id, id)`,
	}
	for _, statement := range stmts {
		if _, err := db.Exec(statement); err != nil {
			return fmt.Errorf("migrate: %w", err)
		}
	}

	// Existing course databases are upgraded in place; no prior data is removed.
	columns := map[string]string{
		"target_unique_id":           "TEXT NOT NULL DEFAULT ''",
		"reserved_by":                "TEXT NOT NULL DEFAULT ''",
		"source_element_id":          "TEXT NOT NULL DEFAULT ''",
		"dimension_definition":       "TEXT NOT NULL DEFAULT ''",
		"fire_minutes":               "INTEGER NOT NULL DEFAULT 0",
		"fire_certificate_reference": "TEXT NOT NULL DEFAULT ''",
		"fire_certificate_verified":  "INTEGER NOT NULL DEFAULT 0",
		"review_note":                "TEXT NOT NULL DEFAULT ''",
		"revision":                   "INTEGER NOT NULL DEFAULT 1",
		"raw_parameters_json":        "TEXT NOT NULL DEFAULT '[]'",
		"field_sources_json":         "TEXT NOT NULL DEFAULT '{}'",
		"mapping_warnings_json":      "TEXT NOT NULL DEFAULT '[]'",
		"updated_at":                 "DATETIME NOT NULL DEFAULT ''",
	}
	for name, definition := range columns {
		if err := ensureColumn(db, "passports", name, definition); err != nil {
			return err
		}
	}
	_, _ = db.Exec(`UPDATE passports SET reuse_status='Pending' WHERE reuse_status=''`)
	_, _ = db.Exec(`UPDATE passports SET status='Unavailable' WHERE reuse_status<>'Approved' AND status='Available'`)
	_, _ = db.Exec(`UPDATE passports SET updated_at=created_at WHERE updated_at=''`)
	return nil
}

func ensureColumn(db *sql.DB, table, name, definition string) error {
	rows, err := db.Query(`PRAGMA table_info(` + table + `)`)
	if err != nil {
		return err
	}
	defer rows.Close()
	for rows.Next() {
		var cid int
		var column, columnType string
		var notNull, primaryKey int
		var defaultValue any
		if err := rows.Scan(&cid, &column, &columnType, &notNull, &defaultValue, &primaryKey); err != nil {
			return err
		}
		if column == name {
			return nil
		}
	}
	if _, err := db.Exec(`ALTER TABLE ` + table + ` ADD COLUMN ` + name + ` ` + definition); err != nil {
		return fmt.Errorf("migrate add %s: %w", name, err)
	}
	return nil
}
