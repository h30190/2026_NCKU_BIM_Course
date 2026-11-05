package main

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"net/http"
	"time"
)

// POST /snapshot — body 是 SnapshotRequest（JSON，不是 multipart）。
// 建一筆新的 model_versions，把 elements 全部塞進 element_snapshots。
func (s *Server) handleCreateSnapshot(w http.ResponseWriter, r *http.Request) {
	var req SnapshotRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeErr(w, http.StatusBadRequest, "JSON 解析失敗: "+err.Error())
		return
	}
	if len(req.Elements) == 0 {
		writeErr(w, http.StatusBadRequest, "elements 不能是空的")
		return
	}

	tx, err := s.DB.Begin()
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer tx.Rollback() // 已經 Commit 的話這行沒作用，失敗時負責清掉

	now := time.Now().UTC().Format("2006-01-02 15:04:05")
	res, err := tx.Exec(
		`INSERT INTO model_versions (label, model_name, created_at) VALUES (?,?,?)`,
		req.Label, req.ModelName, now,
	)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	versionID, _ := res.LastInsertId()

	// 沒給 label 的話，預設用 "V{id}"。
	label := req.Label
	if label == "" {
		label = fmt.Sprintf("V%d", versionID)
		if _, err := tx.Exec(`UPDATE model_versions SET label=? WHERE id=?`, label, versionID); err != nil {
			writeErr(w, 500, err.Error())
			return
		}
	}

	stmt, err := tx.Prepare(`INSERT INTO element_snapshots
		(version_id, unique_id, element_id, type_name, material, quantity, position_x, position_y, position_z, level_name)
		VALUES (?,?,?,?,?,?,?,?,?,?)`)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer stmt.Close()

	for _, el := range req.Elements {
		if el.UniqueID == "" {
			writeErr(w, http.StatusBadRequest, "每個元件都要有 uniqueId")
			return
		}
		if _, err := stmt.Exec(versionID, el.UniqueID, el.ElementID, el.TypeName, el.Material,
			el.Quantity, el.PositionX, el.PositionY, el.PositionZ, el.LevelName); err != nil {
			writeErr(w, 500, err.Error())
			return
		}
	}

	if err := tx.Commit(); err != nil {
		writeErr(w, 500, err.Error())
		return
	}

	writeJSON(w, http.StatusCreated, ModelVersion{
		ID: versionID, Label: label, ModelName: req.ModelName,
		CreatedAt: now, ElementCount: len(req.Elements),
	})
}

// GET /versions — 列出所有已存的版本，含每版的元件數。
func (s *Server) handleListVersions(w http.ResponseWriter, r *http.Request) {
	rows, err := s.DB.Query(`
		SELECT v.id, v.label, v.model_name, v.created_at,
		       (SELECT COUNT(*) FROM element_snapshots e WHERE e.version_id = v.id) AS element_count
		FROM model_versions v
		ORDER BY v.id ASC`)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer rows.Close()

	out := []ModelVersion{}
	for rows.Next() {
		var v ModelVersion
		if err := rows.Scan(&v.ID, &v.Label, &v.ModelName, &v.CreatedAt, &v.ElementCount); err != nil {
			writeErr(w, 500, err.Error())
			return
		}
		out = append(out, v)
	}
	writeJSON(w, http.StatusOK, out)
}

// 內部工具：撈一個版本底下所有元件的 snapshot，用 uniqueId 當 key 方便比對。
func loadSnapshotMap(db *sql.DB, versionID int64) (map[string]ElementSnapshot, error) {
	rows, err := db.Query(`SELECT unique_id, element_id, type_name, material, quantity,
		position_x, position_y, position_z, level_name
		FROM element_snapshots WHERE version_id = ?`, versionID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	out := map[string]ElementSnapshot{}
	for rows.Next() {
		var e ElementSnapshot
		if err := rows.Scan(&e.UniqueID, &e.ElementID, &e.TypeName, &e.Material, &e.Quantity,
			&e.PositionX, &e.PositionY, &e.PositionZ, &e.LevelName); err != nil {
			return nil, err
		}
		out[e.UniqueID] = e
	}
	return out, nil
}
