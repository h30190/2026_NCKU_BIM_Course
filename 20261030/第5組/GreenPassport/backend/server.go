package main

import (
	"database/sql"
	"net/http"
)

type Server struct {
	DB *sql.DB
}

func NewServer(db *sql.DB) *Server {
	return &Server{DB: db}
}

func (s *Server) routes(mux *http.ServeMux) {
	mux.HandleFunc("POST /snapshot", revitWriteOnly(s.handleCreateSnapshot))
	mux.HandleFunc("GET /versions", s.handleListVersions)
	mux.HandleFunc("GET /diff", s.handleDiff)
	mux.HandleFunc("POST /passports", revitWriteOnly(s.handleCreatePassport))
	mux.HandleFunc("GET /passports", s.handleListPassports)
	mux.HandleFunc("POST /passports/match", revitWriteOnly(s.handleMatchPassports))
	mux.HandleFunc("PATCH /passports/{id}", revitWriteOnly(s.handlePatchPassport))
	mux.HandleFunc("PATCH /passports/{id}/review", revitWriteOnly(s.handleReviewPassport))
	mux.HandleFunc("GET /health", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]string{"ok": "true"})
	})
	// 網頁版綠色護照資料庫（瀏覽/搜尋/篩選用，仿 RFI 圖台的風格）。
	// 跟 /passports 同一個 origin，不用開 CORS。
	mux.HandleFunc("GET /dashboard", func(w http.ResponseWriter, r *http.Request) {
		http.ServeFile(w, r, "static/dashboard.html")
	})
}

// The dashboard is intentionally read-only. Mutation and matching endpoints accept
// only local Revit HttpClient calls carrying this marker and no browser Origin.
func revitWriteOnly(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Origin") != "" || r.Header.Get("X-Green-Passport-Client") != "RevitPlugin" {
			writeErr(w, http.StatusForbidden, "此端點只允許本機 Revit 外掛使用；網頁為唯讀")
			return
		}
		next(w, r)
	}
}
