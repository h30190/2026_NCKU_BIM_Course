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
	mux.HandleFunc("POST /api/projects", s.handleRegisterProject)
	mux.HandleFunc("GET /api/projects/{id}/snapshots", s.handleListSnapshots)
	mux.HandleFunc("DELETE /api/projects/{id}", s.handleDeleteProject)
	mux.HandleFunc("GET /api/snapshots/{id}", s.handleGetSnapshot)
	mux.HandleFunc("DELETE /api/snapshots/{id}", s.handleDeleteSnapshot)
	mux.HandleFunc("GET /api/snapshots/{id}/excel", s.handleDownloadExcel)
	mux.HandleFunc("GET /api/compare", s.handleCompare)
	mux.HandleFunc("GET /api/blcc", s.handleBlcc)
	mux.HandleFunc("GET /api/mapping", s.handleMapping)
	mux.HandleFunc("PATCH /api/mapping/{elementId}", s.handleMappingUpdate)
	mux.HandleFunc("GET /api/proposals", s.handleListProposals)
	mux.HandleFunc("GET /api/proposals/chosen", s.handleChosenProposal)
	mux.HandleFunc("PATCH /api/proposals/{id}", s.handleChooseProposal)
	mux.HandleFunc("DELETE /api/proposals/{id}", s.handleDeleteProposal)
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

// POST /api/projects — body: {key, title?}。外掛在 Revit 開啟模型時登記案子（key = 模型檔名），
// 還沒上傳快照也會出現在左側列表；已存在時只更新標題。
func (s *Server) handleRegisterProject(w http.ResponseWriter, r *http.Request) {
	var body struct{ Key, Title string }
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 1<<16)).Decode(&body); err != nil {
		writeErr(w, http.StatusBadRequest, "JSON 無法解析："+err.Error())
		return
	}
	p, err := s.registerProject(body.Key, body.Title)
	var bad errBadSnapshot
	if errors.As(err, &bad) {
		writeErr(w, http.StatusBadRequest, bad.msg)
		return
	}
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, p)
}

func (s *Server) registerProject(key, title string) (Project, error) {
	p := Project{Key: strings.TrimSpace(key), Title: strings.TrimSpace(title)}
	if p.Key == "" {
		return p, errBadSnapshot{"key 必填"}
	}
	if p.Title == "" {
		p.Title = p.Key
	}
	if _, err := s.DB.Exec(`INSERT INTO projects (key, title) VALUES (?, ?)
		ON CONFLICT(key) DO UPDATE SET title = excluded.title`, p.Key, p.Title); err != nil {
		return p, err
	}
	err := s.DB.QueryRow(`SELECT p.id, COUNT(s.id) FROM projects p LEFT JOIN snapshots s ON s.project_id = p.id
		WHERE p.key = ? GROUP BY p.id`, p.Key).Scan(&p.ID, &p.SnapshotCount)
	return p, err
}

// ImportMappingCases 把材料對照表裡出現過、但還沒有案子的模型登記成案子，只在第一次執行。
// 之後使用者刪掉的案子不會因為對照表還有資料又冒出來。
func (s *Server) ImportMappingCases() (int, error) {
	if s.Mapping == nil {
		return 0, nil
	}
	var done int
	if err := s.DB.QueryRow(`SELECT COUNT(*) FROM meta WHERE key = 'mapping-cases'`).Scan(&done); err != nil || done > 0 {
		return 0, err
	}
	names, err := s.Mapping.Projects()
	if err != nil {
		return 0, err
	}
	n := 0
	for _, name := range names {
		var exists int
		if err := s.DB.QueryRow(`SELECT COUNT(*) FROM projects WHERE key = ?`, name).Scan(&exists); err != nil {
			return n, err
		}
		if exists > 0 {
			continue
		}
		if _, err := s.registerProject(name, name); err != nil {
			return n, err
		}
		n++
	}
	_, err = s.DB.Exec(`INSERT INTO meta (key, value) VALUES ('mapping-cases', '1')`)
	return n, err
}

// GET /api/projects — 案子列表，附最新快照的等級、CFR、時間；有快照的依最近上傳排前面，
// 只登記、還沒評估的排在後面（依登記先後）。
func (s *Server) handleListProjects(w http.ResponseWriter, r *http.Request) {
	rows, err := s.DB.Query(`SELECT p.id, p.key, p.title, COUNT(s.id), MAX(s.id)
		FROM projects p LEFT JOIN snapshots s ON s.project_id = p.id
		GROUP BY p.id ORDER BY MAX(s.id) IS NULL, MAX(s.id) DESC, p.id`)
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

// DELETE /api/snapshots/{id} — 刪除一筆快照與明細。案件（對應一個模型）保留，刪光快照就回到「尚未評估」。
func (s *Server) handleDeleteSnapshot(w http.ResponseWriter, r *http.Request) {
	id, ok := pathID(w, r)
	if !ok {
		return
	}
	tx, err := s.DB.Begin()
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer tx.Rollback()

	var projID int64
	if err := tx.QueryRow(`SELECT project_id FROM snapshots WHERE id = ?`, id).Scan(&projID); errors.Is(err, sql.ErrNoRows) {
		writeErr(w, http.StatusNotFound, "找不到這筆快照")
		return
	} else if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	err = execAll(tx,
		[]string{`DELETE FROM snapshot_items WHERE snapshot_id = ?`, `DELETE FROM snapshots WHERE id = ?`}, id)
	if err == nil {
		err = tx.Commit()
	}
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]int64{"id": id, "projectId": projID})
}

// DELETE /api/projects/{id} — 刪除案子與它所有的快照、明細。
func (s *Server) handleDeleteProject(w http.ResponseWriter, r *http.Request) {
	id, ok := pathID(w, r)
	if !ok {
		return
	}
	tx, err := s.DB.Begin()
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer tx.Rollback()

	var n int
	err = execAll(tx, []string{
		`DELETE FROM snapshot_items WHERE snapshot_id IN (SELECT id FROM snapshots WHERE project_id = ?)`,
		`DELETE FROM snapshots WHERE project_id = ?`,
	}, id)
	if err == nil {
		var res sql.Result
		if res, err = tx.Exec(`DELETE FROM projects WHERE id = ?`, id); err == nil {
			affected, _ := res.RowsAffected()
			n = int(affected)
		}
	}
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	if n == 0 {
		writeErr(w, http.StatusNotFound, "找不到這個案子")
		return
	}
	if err := tx.Commit(); err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]int64{"id": id})
}

func execAll(tx *sql.Tx, stmts []string, args ...any) error {
	for _, q := range stmts {
		if _, err := tx.Exec(q, args...); err != nil {
			return err
		}
	}
	return nil
}

// SeedIfEmpty 在第一次啟動、資料庫沒有任何案子時，匯入資料夾裡的 *.json 快照（依檔名排序）。
// 教室沒有 Revit 2026 時，用事先產生的快照 demo 網頁。匯入過一次就記在 meta 表，
// 之後使用者把案子刪光也不會再匯入（刪 db 檔重來才會）。
func (s *Server) SeedIfEmpty(dir string) (int, error) {
	var seeded, n int
	if err := s.DB.QueryRow(`SELECT COUNT(*) FROM meta WHERE key = 'seeded'`).Scan(&seeded); err != nil || seeded > 0 {
		return 0, err
	}
	if err := s.DB.QueryRow(`SELECT COUNT(*) FROM projects`).Scan(&n); err != nil {
		return 0, err
	}
	if _, err := s.DB.Exec(`INSERT INTO meta (key, value) VALUES ('seeded', '1')`); err != nil || n > 0 {
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
