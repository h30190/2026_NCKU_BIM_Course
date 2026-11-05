package main

import (
	"database/sql"
	"fmt"
	"os"
	"path/filepath"
)

// OpenDB opens (and creates) the SQLite file, runs migrations.
func OpenDB(dbPath string) (*sql.DB, error) {
	if err := os.MkdirAll(filepath.Dir(dbPath), 0o755); err != nil {
		return nil, err
	}
	db, err := sql.Open("sqlite", dbPath+"?_pragma=foreign_keys(1)")
	if err != nil {
		return nil, err
	}
	if err := migrate(db); err != nil {
		db.Close()
		return nil, err
	}
	return db, nil
}

// 四張表對應控制台計畫書 5.2 節。快照上傳後不可修改，所以 snapshots 沒有 updated_at。
func migrate(db *sql.DB) error {
	stmts := []string{
		`CREATE TABLE IF NOT EXISTS projects (
			id INTEGER PRIMARY KEY AUTOINCREMENT,
			key TEXT NOT NULL UNIQUE,
			title TEXT NOT NULL DEFAULT '',
			created_at DATETIME NOT NULL DEFAULT (datetime('now'))
		)`,
		`CREATE TABLE IF NOT EXISTS snapshots (
			id INTEGER PRIMARY KEY AUTOINCREMENT,
			project_id INTEGER NOT NULL REFERENCES projects(id),
			name TEXT NOT NULL,
			created_at TEXT NOT NULL,
			uploaded_at DATETIME NOT NULL DEFAULT (datetime('now')),
			plugin_version TEXT NOT NULL DEFAULT '',
			manual TEXT NOT NULL DEFAULT '',
			eec REAL NOT NULL DEFAULT 0,
			eecc REAL NOT NULL DEFAULT 0,
			cfr REAL NOT NULL DEFAULT 0,
			grade TEXT NOT NULL DEFAULT '',
			inputs_json TEXT NOT NULL DEFAULT '{}',
			shape_json TEXT NOT NULL DEFAULT 'null',
			result_json TEXT NOT NULL DEFAULT '{}',
			excel_blob BLOB
		)`,
		`CREATE TABLE IF NOT EXISTS snapshot_items (
			id INTEGER PRIMARY KEY AUTOINCREMENT,
			snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
			side TEXT NOT NULL,
			blcc_id TEXT NOT NULL DEFAULT '',
			category TEXT NOT NULL DEFAULT '',
			category_name TEXT NOT NULL DEFAULT '',
			name TEXT NOT NULL DEFAULT '',
			area REAL NOT NULL DEFAULT 0,
			new_ef REAL NOT NULL DEFAULT 0,
			renew_ef REAL NOT NULL DEFAULT 0,
			new_carbon REAL NOT NULL DEFAULT 0,
			renew_carbon REAL NOT NULL DEFAULT 0,
			carbon REAL NOT NULL DEFAULT 0,
			source TEXT NOT NULL DEFAULT '',
			elements_json TEXT NOT NULL DEFAULT '[]'
		)`,
		// 定位請求（P3 才用到，P0 先建表）
		`CREATE TABLE IF NOT EXISTS locate_requests (
			id INTEGER PRIMARY KEY AUTOINCREMENT,
			project_key TEXT NOT NULL,
			unique_ids_json TEXT NOT NULL DEFAULT '[]',
			label TEXT NOT NULL DEFAULT '',
			status TEXT NOT NULL DEFAULT 'pending',
			created_at DATETIME NOT NULL DEFAULT (datetime('now'))
		)`,
		// 雜項旗標，例如範例快照是否已匯入過
		`CREATE TABLE IF NOT EXISTS meta (
			key TEXT PRIMARY KEY,
			value TEXT NOT NULL DEFAULT ''
		)`,
		`CREATE INDEX IF NOT EXISTS idx_snapshots_project ON snapshots(project_id)`,
		`CREATE INDEX IF NOT EXISTS idx_items_snapshot ON snapshot_items(snapshot_id)`,
	}
	for _, s := range stmts {
		if _, err := db.Exec(s); err != nil {
			return fmt.Errorf("migrate: %w", err)
		}
	}
	// 後加的欄位：舊資料庫沒有就補上，不必刪檔重建
	return addColumn(db, "snapshots", "analysis_json", `TEXT NOT NULL DEFAULT 'null'`)
}

func addColumn(db *sql.DB, table, column, def string) error {
	var n int
	if err := db.QueryRow(`SELECT COUNT(*) FROM pragma_table_info(?) WHERE name = ?`, table, column).Scan(&n); err != nil {
		return fmt.Errorf("migrate: %w", err)
	}
	if n > 0 {
		return nil
	}
	if _, err := db.Exec(fmt.Sprintf(`ALTER TABLE %s ADD COLUMN %s %s`, table, column, def)); err != nil {
		return fmt.Errorf("migrate: %w", err)
	}
	return nil
}
