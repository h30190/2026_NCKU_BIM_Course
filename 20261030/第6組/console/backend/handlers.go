package main

import (
	"database/sql"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"
)

type Server struct {
	DB        *sql.DB
	Mapping   *MappingStore  // 材料對照；nil 時相關 API 回 503
	Proposals *ProposalStore // 減碳建議；nil 時相關 API 回 503
}

func NewServer(db *sql.DB) *Server { return &Server{DB: db} }

func (s *Server) routes(mux *http.ServeMux) {
	mux.HandleFunc("GET /api/health", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]string{"ok": "true"})
	})
	mux.HandleFunc("POST /api/snapshots", s.handleUploadSnapshot)
	mux.HandleFunc("GET /api/projects", s.handleListProjects)
	mux.HandleFunc("GET /api/projects/{id}/snapshots", s.handleListSnapshots)
	mux.HandleFunc("GET /api/snapshots/{id}", s.handleGetSnapshot)
	mux.HandleFunc("GET /api/snapshots/{id}/excel", s.handleDownloadExcel)
	mux.HandleFunc("GET /api/compare", s.handleCompare)
	mux.HandleFunc("GET /api/blcc", s.handleBlcc)
	mux.HandleFunc("GET /api/mapping", s.handleMapping)
	mux.HandleFunc("PATCH /api/mapping/{elementId}", s.handleMappingUpdate)
	mux.HandleFunc("GET /api/proposals", s.handleListProposals)
	mux.HandleFunc("GET /api/proposals/chosen", s.handleChosenProposal)
	mux.HandleFunc("PATCH /api/proposals/{id}", s.handleChooseProposal)
	mux.HandleFunc("PATCH /api/proposals/{id}/applied", s.handleProposalApplied)
}

// errBadSnapshot 代表快照內容不合格（回 400），其他錯誤回 500。
type errBadSnapshot struct{ msg string }

func (e errBadSnapshot) Error() string { return e.msg }

// POST /api/snapshots — 外掛上傳評估快照；案子不存在時自動建立。
func (s *Server) handleUploadSnapshot(w http.ResponseWriter, r *http.Request) {
	r.Body = http.MaxBytesReader(w, r.Body, 20<<20)
	var up SnapshotUpload
	if err := json.NewDecoder(r.Body).Decode(&up); err != nil {
		writeErr(w, http.StatusBadRequest, "快照 JSON 無法解析："+err.Error())
		return
	}
	snapID, projID, err := s.insertSnapshot(&up)
	var bad errBadSnapshot
	if errors.As(err, &bad) {
		writeErr(w, http.StatusBadRequest, bad.msg)
		return
	}
	if err != nil {
		writeErr(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeJSON(w, http.StatusCreated, map[string]int64{"id": snapID, "projectId": projID})
}

func (s *Server) insertSnapshot(up *SnapshotUpload) (snapID, projID int64, err error) {
	if up.SchemaVersion != SupportedSchemaVersion {
		return 0, 0, errBadSnapshot{fmt.Sprintf("unsupported schemaVersion %d（控制台支援 %d）", up.SchemaVersion, SupportedSchemaVersion)}
	}
	key := strings.TrimSpace(up.Project.Key)
	if key == "" {
		return 0, 0, errBadSnapshot{"project.key 必填"}
	}
	name := strings.TrimSpace(up.Snapshot.Name)
	if name == "" {
		return 0, 0, errBadSnapshot{"snapshot.name 必填"}
	}
	var head resultHead
	if len(up.Result) == 0 || json.Unmarshal(up.Result, &head) != nil || head.Grade == "" {
		return 0, 0, errBadSnapshot{"result 缺少或沒有 grade"}
	}
	for _, it := range up.Items {
		if it.Side != "design" && it.Side != "baseline" {
			return 0, 0, errBadSnapshot{"items.side 須為 design 或 baseline"}
		}
	}
	var excel []byte
	if up.Excel != "" {
		if excel, err = base64.StdEncoding.DecodeString(up.Excel); err != nil {
			return 0, 0, errBadSnapshot{"excel 不是有效的 base64"}
		}
	}
	createdAt := up.Snapshot.CreatedAt
	if createdAt == "" {
		createdAt = time.Now().Format(time.RFC3339)
	}
	title := up.Project.Title
	if title == "" {
		title = key
	}

	tx, err := s.DB.Begin()
	if err != nil {
		return 0, 0, err
	}
	defer tx.Rollback()

	if _, err = tx.Exec(`INSERT INTO projects (key, title) VALUES (?, ?)
		ON CONFLICT(key) DO UPDATE SET title = excluded.title`, key, title); err != nil {
		return 0, 0, err
	}
	if err = tx.QueryRow(`SELECT id FROM projects WHERE key = ?`, key).Scan(&projID); err != nil {
		return 0, 0, err
	}

	res, err := tx.Exec(`INSERT INTO snapshots
		(project_id, name, created_at, plugin_version, manual, eec, eecc, cfr, grade, inputs_json, shape_json, result_json, analysis_json, excel_blob)
		VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)`,
		projID, name, createdAt, up.Plugin.Version, up.Plugin.Manual,
		head.Eec, head.Eecc, head.Cfr, head.Grade,
		rawOr(up.Inputs, "{}"), rawOr(up.Shape, "null"), string(up.Result), rawOr(up.Analysis, "null"), excel)
	if err != nil {
		return 0, 0, err
	}
	snapID, _ = res.LastInsertId()

	for _, it := range up.Items {
		if _, err = tx.Exec(`INSERT INTO snapshot_items
			(snapshot_id, side, blcc_id, category, category_name, name, area, new_ef, renew_ef, new_carbon, renew_carbon, carbon, source, elements_json)
			VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)`,
			snapID, it.Side, it.BlccID, it.Category, it.CategoryName, it.Name, it.Area, it.NewEf, it.RenewEf,
			it.NewCarbon, it.RenewCarbon, it.Carbon, it.Source, rawOr(it.Elements, "[]")); err != nil {
			return 0, 0, err
		}
	}
	return snapID, projID, tx.Commit()
}

const summaryCols = `s.id, s.project_id, s.name, s.created_at, s.plugin_version, s.manual,
	s.eec, s.eecc, s.cfr, s.grade, s.excel_blob IS NOT NULL`

func scanSummary(row interface{ Scan(...any) error }, x *SnapshotSummary, extra ...any) error {
	return row.Scan(append([]any{&x.ID, &x.ProjectID, &x.Name, &x.CreatedAt, &x.PluginVersion, &x.Manual,
		&x.Eec, &x.Eecc, &x.Cfr, &x.Grade, &x.HasExcel}, extra...)...)
}

// GET /api/projects — 案子列表，附最新快照的等級、CFR、時間；最近更新的排前面。
func (s *Server) handleListProjects(w http.ResponseWriter, r *http.Request) {
	rows, err := s.DB.Query(`SELECT p.id, p.key, p.title, COUNT(s.id), MAX(s.id)
		FROM projects p LEFT JOIN snapshots s ON s.project_id = p.id
		GROUP BY p.id ORDER BY MAX(s.id) DESC`)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	type row struct {
		p      Project
		latest sql.NullInt64
	}
	var list []row
	for rows.Next() {
		var x row
		if err := rows.Scan(&x.p.ID, &x.p.Key, &x.p.Title, &x.p.SnapshotCount, &x.latest); err != nil {
			rows.Close()
			writeErr(w, 500, err.Error())
			return
		}
		list = append(list, x)
	}
	rows.Close()

	out := make([]Project, 0, len(list))
	for _, x := range list {
		if x.latest.Valid {
			var sum SnapshotSummary
			if err := scanSummary(s.DB.QueryRow(`SELECT `+summaryCols+` FROM snapshots s WHERE s.id = ?`, x.latest.Int64), &sum); err != nil {
				writeErr(w, 500, err.Error())
				return
			}
			x.p.Latest = &sum
		}
		out = append(out, x.p)
	}
	writeJSON(w, http.StatusOK, out)
}

// GET /api/projects/{id}/snapshots — 某案子所有快照，由新到舊，不含大型 JSON。
func (s *Server) handleListSnapshots(w http.ResponseWriter, r *http.Request) {
	id, ok := pathID(w, r)
	if !ok {
		return
	}
	rows, err := s.DB.Query(`SELECT `+summaryCols+` FROM snapshots s WHERE s.project_id = ? ORDER BY s.id DESC`, id)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer rows.Close()
	out := []SnapshotSummary{}
	for rows.Next() {
		var x SnapshotSummary
		if err := scanSummary(rows, &x); err != nil {
			writeErr(w, 500, err.Error())
			return
		}
		out = append(out, x)
	}
	writeJSON(w, http.StatusOK, out)
}

// GET /api/snapshots/{id} — 單一快照完整內容與明細。
func (s *Server) handleGetSnapshot(w http.ResponseWriter, r *http.Request) {
	id, ok := pathID(w, r)
	if !ok {
		return
	}
	d, err := s.loadSnapshot(id)
	if errors.Is(err, sql.ErrNoRows) {
		writeErr(w, http.StatusNotFound, "找不到這筆快照")
		return
	}
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, d)
}

// loadSnapshot 讀出單一快照與明細；不存在時回 sql.ErrNoRows。
func (s *Server) loadSnapshot(id int64) (*SnapshotDetail, error) {
	var d SnapshotDetail
	var inputs, shape, result, analysis string
	err := scanSummary(s.DB.QueryRow(`SELECT `+summaryCols+`, p.key, p.title, s.inputs_json, s.shape_json, s.result_json, s.analysis_json
		FROM snapshots s JOIN projects p ON p.id = s.project_id WHERE s.id = ?`, id),
		&d.SnapshotSummary, &d.ProjectKey, &d.ProjectTitle, &inputs, &shape, &result, &analysis)
	if err != nil {
		return nil, err
	}
	d.Inputs, d.Shape, d.Result = json.RawMessage(inputs), json.RawMessage(shape), json.RawMessage(result)
	d.Analysis = json.RawMessage(analysis)

	rows, err := s.DB.Query(`SELECT side, blcc_id, category, category_name, name, area, new_ef, renew_ef,
		new_carbon, renew_carbon, carbon, source, elements_json
		FROM snapshot_items WHERE snapshot_id = ? ORDER BY id`, id)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	d.Items = []Item{}
	for rows.Next() {
		var it Item
		var elements string
		if err := rows.Scan(&it.Side, &it.BlccID, &it.Category, &it.CategoryName, &it.Name, &it.Area, &it.NewEf, &it.RenewEf,
			&it.NewCarbon, &it.RenewCarbon, &it.Carbon, &it.Source, &elements); err != nil {
			return nil, err
		}
		it.Elements = json.RawMessage(elements)
		d.Items = append(d.Items, it)
	}
	return &d, rows.Err()
}

// SeedIfEmpty 在資料庫沒有任何案子時，匯入資料夾裡的 *.json 快照（依檔名排序）。
// 教室沒有 Revit 2026 時，用事先產生的快照 demo 網頁。
func (s *Server) SeedIfEmpty(dir string) (int, error) {
	var n int
	if err := s.DB.QueryRow(`SELECT COUNT(*) FROM projects`).Scan(&n); err != nil || n > 0 {
		return 0, err
	}
	files, err := filepath.Glob(filepath.Join(dir, "*.json"))
	if err != nil {
		return 0, err
	}
	sort.Strings(files)
	imported := 0
	for _, f := range files {
		b, err := os.ReadFile(f)
		if err != nil {
			return imported, err
		}
		var up SnapshotUpload
		if err := json.Unmarshal(b, &up); err != nil {
			return imported, fmt.Errorf("%s: %w", filepath.Base(f), err)
		}
		if _, _, err := s.insertSnapshot(&up); err != nil {
			return imported, fmt.Errorf("%s: %w", filepath.Base(f), err)
		}
		imported++
	}
	return imported, nil
}

func pathID(w http.ResponseWriter, r *http.Request) (int64, bool) {
	id, err := strconv.ParseInt(r.PathValue("id"), 10, 64)
	if err != nil {
		writeErr(w, http.StatusBadRequest, "id 須為數字")
		return 0, false
	}
	return id, true
}

func rawOr(m json.RawMessage, def string) string {
	if len(m) == 0 {
		return def
	}
	return string(m)
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}

func writeErr(w http.ResponseWriter, code int, msg string) {
	writeJSON(w, code, map[string]string{"error": msg})
}
