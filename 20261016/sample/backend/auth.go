package main

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"log"
	"net/http"

	"golang.org/x/crypto/bcrypt"
)

// SeedAdmin 首次啟動建 admin/changeme（只建一次，帳密印在 log 提醒改密碼）。
func SeedAdmin(db *sql.DB) {
	var n int
	if err := db.QueryRow(`SELECT COUNT(*) FROM users`).Scan(&n); err != nil || n > 0 {
		return
	}
	hash, _ := bcrypt.GenerateFromPassword([]byte("changeme"), bcrypt.DefaultCost)
	_, err := db.Exec(`INSERT INTO users (username, password_hash, role, active) VALUES ('admin', ?, 'admin', 1)`, string(hash))
	if err != nil {
		log.Println("seed admin 失敗:", err)
		return
	}
	log.Println("已建立預設帳號 admin / changeme（登入後請改密碼）")
}

type ctxKey string

const userKey ctxKey = "user"

// AuthUser 通過驗證的使用者（含角色）。
type AuthUser struct {
	Username string
	Role     string
}

func newToken() string {
	b := make([]byte, 32)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// POST /api/login — JSON {username, password} → {token, username, role}
func (s *Server) handleLogin(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Username string `json:"username"`
		Password string `json:"password"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.Username == "" {
		writeErr(w, http.StatusBadRequest, "帳號密碼必填")
		return
	}
	var hash, role string
	var active int
	err := s.DB.QueryRow(`SELECT password_hash, role, active FROM users WHERE username=?`, body.Username).Scan(&hash, &role, &active)
	if err != nil || active == 0 || bcrypt.CompareHashAndPassword([]byte(hash), []byte(body.Password)) != nil {
		writeErr(w, http.StatusUnauthorized, "帳號或密碼錯誤")
		return
	}
	tok := newToken()
	if _, err := s.DB.Exec(`INSERT INTO sessions (token, username) VALUES (?, ?)`, tok, body.Username); err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"token": tok, "username": body.Username, "role": role})
}

// POST /api/logout — 刪自己的 token
func (s *Server) handleLogout(w http.ResponseWriter, r *http.Request) {
	if tok := bearer(r); tok != "" {
		_, _ = s.DB.Exec(`DELETE FROM sessions WHERE token=?`, tok)
	}
	writeJSON(w, http.StatusOK, map[string]string{"ok": "true"})
}

// GET /api/me — 回傳登入者（給前端顯示＋判斷權限）
func (s *Server) handleMe(w http.ResponseWriter, r *http.Request) {
	u, ok := r.Context().Value(userKey).(AuthUser)
	if !ok {
		writeErr(w, http.StatusUnauthorized, "未登入")
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"username": u.Username, "role": u.Role})
}

func bearer(r *http.Request) string {
	h := r.Header.Get("Authorization")
	if len(h) > 7 && h[:7] == "Bearer " {
		return h[7:]
	}
	return ""
}

// requireAuth 驗證中介層：GET 放行（課堂投影用），寫入一律要 token。
// minRole: "" = 只要登入；"admin" = 限管理員。
func (s *Server) requireAuth(minRole string, next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodGet && minRole == "" {
			next(w, r) // 讀取開放
			return
		}
		u, ok := s.authUser(r)
		if !ok {
			writeErr(w, http.StatusUnauthorized, "未登入（先到圖台登入）")
			return
		}
		if minRole == "admin" && u.Role != "admin" {
			writeErr(w, http.StatusForbidden, "限管理員")
			return
		}
		if minRole == "member" && u.Role == "viewer" {
			writeErr(w, http.StatusForbidden, "viewer 唯讀")
			return
		}
		next(w, r.WithContext(context.WithValue(r.Context(), userKey, u)))
	}
}

func (s *Server) authUser(r *http.Request) (AuthUser, bool) {
	tok := bearer(r)
	if tok == "" {
		return AuthUser{}, false
	}
	var u AuthUser
	var active int
	err := s.DB.QueryRow(`SELECT s.username, u.role, u.active FROM sessions s
		JOIN users u ON u.username = s.username WHERE s.token=?`, tok).Scan(&u.Username, &u.Role, &active)
	if err != nil || active == 0 {
		return AuthUser{}, false
	}
	return u, true
}

// GET /api/users — 帳號列表（admin）
func (s *Server) handleListUsers(w http.ResponseWriter, r *http.Request) {
	rows, err := s.DB.Query(`SELECT username, role, active, created_at FROM users ORDER BY username`)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer rows.Close()
	out := []User{}
	for rows.Next() {
		var u User
		var active int
		if err := rows.Scan(&u.Username, &u.Role, &active, &u.CreatedAt); err != nil {
			writeErr(w, 500, err.Error())
			return
		}
		u.Active = active == 1
		out = append(out, u)
	}
	writeJSON(w, http.StatusOK, out)
}

// POST /api/users — 新增帳號（admin）：JSON {username, password, role}
func (s *Server) handleCreateUser(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Username string `json:"username"`
		Password string `json:"password"`
		Role     string `json:"role"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.Username == "" || body.Password == "" {
		writeErr(w, http.StatusBadRequest, "帳號密碼必填")
		return
	}
	if body.Role == "" {
		body.Role = "member"
	}
	if !validRole[body.Role] {
		writeErr(w, http.StatusBadRequest, "role 須為 admin/member/viewer")
		return
	}
	hash, err := bcrypt.GenerateFromPassword([]byte(body.Password), bcrypt.DefaultCost)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	if _, err := s.DB.Exec(`INSERT INTO users (username, password_hash, role, active) VALUES (?, ?, ?, 1)`,
		body.Username, string(hash), body.Role); err != nil {
		writeErr(w, http.StatusBadRequest, "帳號已存在？"+err.Error())
		return
	}
	writeJSON(w, http.StatusCreated, map[string]string{"username": body.Username, "role": body.Role})
}

// PATCH /api/users/{username} — 改角色/啟停用（admin）：JSON {role?, active?}
func (s *Server) handlePatchUser(w http.ResponseWriter, r *http.Request) {
	name := r.PathValue("username")
	if name == "" {
		writeErr(w, http.StatusBadRequest, "username 必填")
		return
	}
	var body struct {
		Role   *string `json:"role"`
		Active *bool   `json:"active"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeErr(w, http.StatusBadRequest, "JSON 解析失敗")
		return
	}
	if body.Role != nil {
		if !validRole[*body.Role] {
			writeErr(w, http.StatusBadRequest, "role 須為 admin/member/viewer")
			return
		}
		if _, err := s.DB.Exec(`UPDATE users SET role=? WHERE username=?`, *body.Role, name); err != nil {
			writeErr(w, 500, err.Error())
			return
		}
	}
	if body.Active != nil {
		v := 0
		if *body.Active {
			v = 1
		}
		if _, err := s.DB.Exec(`UPDATE users SET active=? WHERE username=?`, v, name); err != nil {
			writeErr(w, 500, err.Error())
			return
		}
	}
	writeJSON(w, http.StatusOK, map[string]string{"username": name})
}
