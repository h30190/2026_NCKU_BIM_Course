package main

import (
	"bytes"
	"encoding/json"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"

	_ "modernc.org/sqlite"
)

func newTestServer(t *testing.T) *Server {
	t.Helper()
	db, err := OpenDB(filepath.Join(t.TempDir(), "test.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { db.Close() })
	s := NewServer(db, t.TempDir())
	return s
}

func TestIssueCRUD(t *testing.T) {
	s := newTestServer(t)
	SeedAdmin(s.DB)
	mux := http.NewServeMux()
	s.routes(mux)

	// 測試用 token（寫入要登入）
	loginBody := bytes.NewBufferString(`{"username":"admin","password":"changeme"}`)
	lreq := httptest.NewRequest("POST", "/api/login", loginBody)
	lrec := httptest.NewRecorder()
	mux.ServeHTTP(lrec, lreq)
	if lrec.Code != http.StatusOK {
		t.Fatalf("login = %d", lrec.Code)
	}
	var sess map[string]string
	_ = json.Unmarshal(lrec.Body.Bytes(), &sess)
	auth := "Bearer " + sess["token"]

	// 先建一個可指派的帳號（assignee 驗證用）
	cu := bytes.NewBufferString(`{"username":"s01","password":"pw","role":"member"}`)
	creq := httptest.NewRequest("POST", "/api/users", cu)
	creq.Header.Set("Authorization", auth)
	crec := httptest.NewRecorder()
	mux.ServeHTTP(crec, creq)

	// create（multipart，不帶截圖）
	var b bytes.Buffer
	w := multipart.NewWriter(&b)
	_ = w.WriteField("title", "門打不開")
	_ = w.WriteField("description", "FL1 門 M1 被牆卡住")
	_ = w.WriteField("reporter", "學生A")
	_ = w.WriteField("source", "revit")
	_ = w.WriteField("revit_element_id", "12345")
	_ = w.WriteField("view_name", "FL1 平面")
	_ = w.WriteField("revit_view_id", "abc-123")
	_ = w.WriteField("model_name", "小屋.rvt")
	w.Close()
	req := httptest.NewRequest("POST", "/api/issues", &b)
	req.Header.Set("Content-Type", w.FormDataContentType())
	req.Header.Set("Authorization", auth)
	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusCreated {
		t.Fatalf("create = %d, body=%s", rec.Code, rec.Body.String())
	}
	var created Issue
	if err := json.Unmarshal(rec.Body.Bytes(), &created); err != nil {
		t.Fatal(err)
	}
	if created.ID == 0 || created.Status != "open" {
		t.Fatalf("unexpected created: %+v", created)
	}
	if created.ViewName != "FL1 平面" || created.RevitViewID != "abc-123" {
		t.Fatalf("view fields not round-tripped: %+v", created)
	}
	if created.ModelName != "小屋.rvt" {
		t.Fatalf("model_name not round-tripped: %+v", created)
	}

	// list + status 篩選
	req = httptest.NewRequest("GET", "/api/issues?status=open", nil)
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("list = %d", rec.Code)
	}
	var list []Issue
	if err := json.Unmarshal(rec.Body.Bytes(), &list); err != nil {
		t.Fatal(err)
	}
	if len(list) != 1 {
		t.Fatalf("list len = %d", len(list))
	}

	// patch 狀態
	patchBody := bytes.NewBufferString(`{"status":"in_progress","assignee":"s01"}`)
	req = httptest.NewRequest("PATCH", "/api/issues/1", patchBody)
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", auth)
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("patch = %d, body=%s", rec.Code, rec.Body.String())
	}
	var patched Issue
	_ = json.Unmarshal(rec.Body.Bytes(), &patched)
	if patched.Status != "in_progress" || patched.Assignee != "s01" {
		t.Fatalf("unexpected patched: %+v", patched)
	}

	// 非法狀態要 400
	bad := bytes.NewBufferString(`{"status":"nope"}`)
	req = httptest.NewRequest("PATCH", "/api/issues/1", bad)
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", auth)
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("bad status = %d, want 400", rec.Code)
	}

	// 未帶 token 的寫入要 401
	anon := bytes.NewBufferString(`{"status":"closed"}`)
	req = httptest.NewRequest("PATCH", "/api/issues/1", anon)
	req.Header.Set("Content-Type", "application/json")
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("anon patch = %d, want 401", rec.Code)
	}

	// 封存：預設列表看不到，archived=1 才看得到
	arc := bytes.NewBufferString(`{"archived":true}`)
	req = httptest.NewRequest("PATCH", "/api/issues/1", arc)
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", auth)
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("archive = %d, body=%s", rec.Code, rec.Body.String())
	}
	req = httptest.NewRequest("GET", "/api/issues", nil)
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	var hidden []Issue
	_ = json.Unmarshal(rec.Body.Bytes(), &hidden)
	if len(hidden) != 0 {
		t.Fatalf("archived issue should be hidden, got %d", len(hidden))
	}
	req = httptest.NewRequest("GET", "/api/issues?archived=1", nil)
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	var shown []Issue
	_ = json.Unmarshal(rec.Body.Bytes(), &shown)
	if len(shown) != 1 || !shown[0].Archived {
		t.Fatalf("archived=1 should show it: %+v", shown)
	}
}

func TestAuthAndUsers(t *testing.T) {
	s := newTestServer(t)
	SeedAdmin(s.DB)
	mux := http.NewServeMux()
	s.routes(mux)

	// 未登入寫入 → 401
	req := httptest.NewRequest("POST", "/api/issues", nil)
	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("anon post = %d, want 401", rec.Code)
	}

	// 登入 seed admin
	loginBody := bytes.NewBufferString(`{"username":"admin","password":"changeme"}`)
	req = httptest.NewRequest("POST", "/api/login", loginBody)
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("login = %d, body=%s", rec.Code, rec.Body.String())
	}
	var sess map[string]string
	_ = json.Unmarshal(rec.Body.Bytes(), &sess)
	auth := "Bearer " + sess["token"]

	// 建 member＋viewer
	for _, u := range []string{`{"username":"s01","password":"pw1","role":"member"}`, `{"username":"g1","password":"pw2","role":"viewer"}`} {
		req = httptest.NewRequest("POST", "/api/users", bytes.NewBufferString(u))
		req.Header.Set("Authorization", auth)
		rec = httptest.NewRecorder()
		mux.ServeHTTP(rec, req)
		if rec.Code != http.StatusCreated {
			t.Fatalf("create user = %d, body=%s", rec.Code, rec.Body.String())
		}
	}

	// viewer 登入後寫入 → 403
	lb := bytes.NewBufferString(`{"username":"g1","password":"pw2"}`)
	req = httptest.NewRequest("POST", "/api/login", lb)
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	var vsess map[string]string
	_ = json.Unmarshal(rec.Body.Bytes(), &vsess)
	req = httptest.NewRequest("POST", "/api/issues", nil)
	req.Header.Set("Authorization", "Bearer "+vsess["token"])
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusForbidden && rec.Code != http.StatusUnauthorized {
		t.Fatalf("viewer post = %d, want 403", rec.Code)
	}

	// 指派給不存在的帳號 → 400（先建一筆議題）
	var b bytes.Buffer
	w := multipart.NewWriter(&b)
	_ = w.WriteField("title", "t")
	w.Close()
	req = httptest.NewRequest("POST", "/api/issues", &b)
	req.Header.Set("Content-Type", w.FormDataContentType())
	req.Header.Set("Authorization", auth)
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusCreated {
		t.Fatalf("member create = %d, body=%s", rec.Code, rec.Body.String())
	}
	bad := bytes.NewBufferString(`{"assignee":"nobody"}`)
	req = httptest.NewRequest("PATCH", "/api/issues/1", bad)
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", auth)
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("bad assignee = %d, want 400", rec.Code)
	}
	// 指派給 s01 → 200
	good := bytes.NewBufferString(`{"assignee":"s01"}`)
	req = httptest.NewRequest("PATCH", "/api/issues/1", good)
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Authorization", auth)
	rec = httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("good assignee = %d, body=%s", rec.Code, rec.Body.String())
	}
}

func TestParseIFCIdentity(t *testing.T) {
	ifc := `ISO-10303-21;
#1=IFCWALL('2aXyzAAA00001',#42,'Wall-001',$,'Basic Wall 15cm',$,$);
#2=IFCDOOR('2aXyzAAA00002',#42,'Door-M1',$,'Single Door',$,$);
#10=IFCBUILDINGSTOREY('storey111',#42,'Level 1',$,$,$,$,$,$);
#20=IFCRELCONTAINEDINSPATIALSTRUCTURE('rel1',#42,$,$,(#1,#2),#10);
`
	p := filepath.Join(t.TempDir(), "a.ifc")
	if err := os.WriteFile(p, []byte(ifc), 0o644); err != nil {
		t.Fatal(err)
	}
	els, err := ParseIFCIdentity(p)
	if err != nil {
		t.Fatal(err)
	}
	if len(els) != 2 {
		t.Fatalf("elements = %d, want 2: %+v", len(els), els)
	}
}
