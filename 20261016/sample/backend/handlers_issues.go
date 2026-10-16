package main

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

type Server struct {
	DB            *sql.DB
	DataDir       string // e.g. ./data ; screenshots/ 與 models/ 放底下
	ScreenshotDir string
	ModelDir      string
}

func NewServer(db *sql.DB, dataDir string) *Server {
	return &Server{
		DB:            db,
		DataDir:       dataDir,
		ScreenshotDir: filepath.Join(dataDir, "screenshots"),
		ModelDir:      filepath.Join(dataDir, "models"),
	}
}

func (s *Server) routes(mux *http.ServeMux) {
	// 議題：讀開放（課堂投影），寫要登入（member 以上）
	mux.HandleFunc("POST /api/issues", s.requireAuth("member", s.handleCreateIssue))
	mux.HandleFunc("GET /api/issues", s.handleListIssues)
	mux.HandleFunc("GET /api/issues/{id}", s.handleGetIssue)
	mux.HandleFunc("PATCH /api/issues/{id}", s.requireAuth("member", s.handlePatchIssue))
	// 模型：上傳要登入，下載/列表開放
	mux.HandleFunc("POST /api/models", s.requireAuth("member", s.handleUploadModel))
	mux.HandleFunc("GET /api/models/{id}/file", s.handleDownloadModel)
	mux.HandleFunc("GET /api/models/{id}/elements", s.handleListElements)
	// 帳號
	mux.HandleFunc("POST /api/login", s.handleLogin)
	mux.HandleFunc("POST /api/logout", s.handleLogout)
	mux.HandleFunc("GET /api/me", s.requireAuth("", s.handleMe))
	mux.HandleFunc("GET /api/users", s.requireAuth("admin", s.handleListUsers))
	mux.HandleFunc("POST /api/users", s.requireAuth("admin", s.handleCreateUser))
	mux.HandleFunc("PATCH /api/users/{username}", s.requireAuth("admin", s.handlePatchUser))
	// 截圖靜態檔（第一版最簡，不做鑑權，僅課堂內網用）
	mux.Handle("GET /screenshots/", http.StripPrefix("/screenshots/", http.FileServer(http.Dir(s.ScreenshotDir))))
	mux.HandleFunc("GET /api/health", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]string{"ok": "true"})
	})
}

// POST /api/issues — multipart: 欄位 + screenshot(可選)
func (s *Server) handleCreateIssue(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseMultipartForm(20 << 20); err != nil && err != http.ErrNotMultipart {
		writeErr(w, http.StatusBadRequest, "parse form: "+err.Error())
		return
	}
	title := strings.TrimSpace(r.FormValue("title"))
	if title == "" {
		writeErr(w, http.StatusBadRequest, "title 必填")
		return
	}
	status := r.FormValue("status")
	if status == "" {
		status = "open"
	}
	if !validStatus[status] {
		writeErr(w, http.StatusBadRequest, "status 須為 open/in_progress/closed")
		return
	}
	source := r.FormValue("source")
	if source == "" {
		source = "manual"
	}

	var modelID *int64
	if v := strings.TrimSpace(r.FormValue("model_id")); v != "" {
		n, err := strconv.ParseInt(v, 10, 64)
		if err != nil {
			writeErr(w, http.StatusBadRequest, "model_id 須為數字")
			return
		}
		modelID = &n
	}

	// 截圖（可空）
	screenshotName := ""
	if f, hdr, err := r.FormFile("screenshot"); err == nil {
		defer f.Close()
		if err := os.MkdirAll(s.ScreenshotDir, 0o755); err != nil {
			writeErr(w, 500, err.Error())
			return
		}
		ext := filepath.Ext(hdr.Filename)
		if ext == "" {
			ext = ".png"
		}
		screenshotName = fmt.Sprintf("%d%s", time.Now().UnixNano(), ext)
		out, err := os.Create(filepath.Join(s.ScreenshotDir, screenshotName))
		if err != nil {
			writeErr(w, 500, err.Error())
			return
		}
		if _, err := io.Copy(out, f); err != nil {
			out.Close()
			writeErr(w, 500, err.Error())
			return
		}
		out.Close()
	}

	now := time.Now().UTC().Format("2006-01-02 15:04:05")
	res, err := s.DB.Exec(`INSERT INTO issues
		(title,description,status,reporter,assignee,source,revit_element_id,ifc_global_id,model_id,screenshot,position,view_name,revit_view_id,model_name,created_at,updated_at)
		VALUES (?,?,?,?,?,?,?,?,?,?,?, ?,?,?, ?, ?)`,
		title, r.FormValue("description"), status,
		r.FormValue("reporter"), r.FormValue("assignee"), source,
		r.FormValue("revit_element_id"), r.FormValue("ifc_global_id"), modelID,
		screenshotName, r.FormValue("position"),
		r.FormValue("view_name"), r.FormValue("revit_view_id"), r.FormValue("model_name"), now, now)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	id, _ := res.LastInsertId()
	issue, err := getIssueByID(s.DB, id)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusCreated, issue)
}

// GET /api/issues?status=open&archived=1
// archived 預設只顯示未封存；archived=1 只看已封存；archived=all 全看。
func (s *Server) handleListIssues(w http.ResponseWriter, r *http.Request) {
	base := `SELECT i.id,i.title,i.description,i.status,i.reporter,i.assignee,i.source,
		COALESCE(i.revit_element_id,''),COALESCE(i.ifc_global_id,''),i.model_id,
		COALESCE(i.screenshot,''),COALESCE(i.position,''),
		COALESCE(i.view_name,''),COALESCE(i.revit_view_id,''),
		COALESCE(NULLIF(i.model_name,''), m.filename, ''),
		i.archived,i.created_at,i.updated_at
		FROM issues i LEFT JOIN models m ON m.id = i.model_id`
	var conds []string
	var args []any
	if st := r.URL.Query().Get("status"); st != "" {
		if !validStatus[st] {
			writeErr(w, http.StatusBadRequest, "status 須為 open/in_progress/closed")
			return
		}
		conds = append(conds, "i.status=?")
		args = append(args, st)
	}
	switch r.URL.Query().Get("archived") {
	case "1":
		conds = append(conds, "i.archived=1")
	case "all":
		// 不篩
	default:
		conds = append(conds, "i.archived=0")
	}
	q := base
	if len(conds) > 0 {
		q += " WHERE " + strings.Join(conds, " AND ")
	}
	q += " ORDER BY i.id DESC"
	rows, err := s.DB.Query(q, args...)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer rows.Close()
	out := []Issue{}
	for rows.Next() {
		var it Issue
		if err := rows.Scan(&it.ID, &it.Title, &it.Description, &it.Status,
			&it.Reporter, &it.Assignee, &it.Source, &it.RevitElementID,
			&it.IFCGlobalID, &it.ModelID, &it.Screenshot, &it.Position,
			&it.ViewName, &it.RevitViewID, &it.ModelName, &it.Archived,
			&it.CreatedAt, &it.UpdatedAt); err != nil {
			writeErr(w, 500, err.Error())
			return
		}
		out = append(out, it)
	}
	writeJSON(w, http.StatusOK, out)
}

// GET /api/issues/{id}
func (s *Server) handleGetIssue(w http.ResponseWriter, r *http.Request) {
	id, err := strconv.ParseInt(r.PathValue("id"), 10, 64)
	if err != nil {
		writeErr(w, http.StatusBadRequest, "id 須為數字")
		return
	}
	it, err := getIssueByID(s.DB, id)
	if err == sql.ErrNoRows {
		writeErr(w, http.StatusNotFound, "找不到議題")
		return
	}
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, it)
}

// PATCH /api/issues/{id} — JSON: {status?, assignee?, archived?}
func (s *Server) handlePatchIssue(w http.ResponseWriter, r *http.Request) {
	id, err := strconv.ParseInt(r.PathValue("id"), 10, 64)
	if err != nil {
		writeErr(w, http.StatusBadRequest, "id 須為數字")
		return
	}
	var body struct {
		Status   *string `json:"status"`
		Assignee *string `json:"assignee"`
		Archived *bool   `json:"archived"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeErr(w, http.StatusBadRequest, "JSON 解析失敗: "+err.Error())
		return
	}
	if body.Status != nil && !validStatus[*body.Status] {
		writeErr(w, http.StatusBadRequest, "status 須為 open/in_progress/closed")
		return
	}
	if body.Status == nil && body.Assignee == nil && body.Archived == nil {
		writeErr(w, http.StatusBadRequest, "至少要帶 status、assignee 或 archived")
		return
	}
	if body.Status != nil {
		if _, err := s.DB.Exec(`UPDATE issues SET status=?, updated_at=datetime('now') WHERE id=?`, *body.Status, id); err != nil {
			writeErr(w, 500, err.Error())
			return
		}
	}
	if body.Assignee != nil {
		// 指派只接受存在的啟用帳號（P1：assignee = username）
		var n int
		if err := s.DB.QueryRow(`SELECT COUNT(*) FROM users WHERE username=? AND active=1`, *body.Assignee).Scan(&n); err != nil || n == 0 {
			writeErr(w, http.StatusBadRequest, "指派對象不是啟用中的帳號")
			return
		}
		if _, err := s.DB.Exec(`UPDATE issues SET assignee=?, updated_at=datetime('now') WHERE id=?`, *body.Assignee, id); err != nil {
			writeErr(w, 500, err.Error())
			return
		}
	}
	if body.Archived != nil {
		v := 0
		if *body.Archived {
			v = 1
		}
		if _, err := s.DB.Exec(`UPDATE issues SET archived=?, updated_at=datetime('now') WHERE id=?`, v, id); err != nil {
			writeErr(w, 500, err.Error())
			return
		}
	}
	it, err := getIssueByID(s.DB, id)
	if err == sql.ErrNoRows {
		writeErr(w, http.StatusNotFound, "找不到議題")
		return
	}
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, it)
}

func getIssueByID(db *sql.DB, id int64) (Issue, error) {
	var it Issue
	err := db.QueryRow(`SELECT i.id,i.title,i.description,i.status,i.reporter,i.assignee,i.source,
		COALESCE(i.revit_element_id,''),COALESCE(i.ifc_global_id,''),i.model_id,
		COALESCE(i.screenshot,''),COALESCE(i.position,''),
		COALESCE(i.view_name,''),COALESCE(i.revit_view_id,''),
		COALESCE(NULLIF(i.model_name,''), m.filename, ''),
		i.archived,i.created_at,i.updated_at
		FROM issues i LEFT JOIN models m ON m.id = i.model_id WHERE i.id=?`, id).Scan(
		&it.ID, &it.Title, &it.Description, &it.Status, &it.Reporter,
		&it.Assignee, &it.Source, &it.RevitElementID, &it.IFCGlobalID,
		&it.ModelID, &it.Screenshot, &it.Position,
		&it.ViewName, &it.RevitViewID, &it.ModelName, &it.Archived, &it.CreatedAt, &it.UpdatedAt)
	return it, err
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}

func writeErr(w http.ResponseWriter, code int, msg string) {
	writeJSON(w, code, map[string]string{"error": msg})
}
