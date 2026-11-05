package main

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"

	_ "modernc.org/sqlite"
)

func newTestMux(t *testing.T) (*Server, *http.ServeMux) {
	t.Helper()
	db, err := OpenDB(filepath.Join(t.TempDir(), "test.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { db.Close() })
	s := NewServer(db)
	mux := http.NewServeMux()
	s.routes(mux)
	return s, mux
}

func do(t *testing.T, mux *http.ServeMux, method, path, body string) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequest(method, path, strings.NewReader(body))
	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, req)
	return rec
}

// 與外掛 SnapshotBuilder 輸出同格式的最小快照
func snapshotJSON(key, name, grade string, cfr float64, schema int) string {
	b, _ := json.Marshal(map[string]any{
		"schemaVersion": schema,
		"plugin":        map[string]string{"version": "1.0.0", "manual": "LEBR-2025"},
		"project":       map[string]string{"key": key, "title": key},
		"snapshot":      map[string]string{"name": name, "createdAt": "2026-10-21T10:00:00+08:00"},
		"inputs":        map[string]any{"aFu": 336, "s": 3},
		"result":        map[string]any{"eec": 119526.0, "eecc": 145861.0, "cfr": cfr, "grade": grade},
		"items": []map[string]any{
			{"side": "design", "blccId": "CW-0", "category": "cw", "name": "傳統 15cm RC 外牆", "area": 436.08, "carbon": 32126.01},
			{"side": "baseline", "blccId": "CW-0", "category": "cw", "name": "傳統 15cm RC 外牆", "area": 436.08, "carbon": 32126.01},
		},
	})
	return string(b)
}

func TestHealth(t *testing.T) {
	_, mux := newTestMux(t)
	if rec := do(t, mux, "GET", "/api/health", ""); rec.Code != http.StatusOK {
		t.Fatalf("health = %d", rec.Code)
	}
}

func TestUploadListAndGet(t *testing.T) {
	_, mux := newTestMux(t)

	rec := do(t, mux, "POST", "/api/snapshots", snapshotJSON("專案1", "方案 A", "1級", 0.1805, 1))
	if rec.Code != http.StatusCreated {
		t.Fatalf("upload = %d, body=%s", rec.Code, rec.Body.String())
	}
	var created struct{ ID, ProjectID int64 }
	_ = json.Unmarshal(rec.Body.Bytes(), &created)

	// 同一個 key 的第二筆快照落在同一個案子，不覆蓋第一筆
	rec = do(t, mux, "POST", "/api/snapshots", snapshotJSON("專案1", "方案 B", "2級", 0.13, 1))
	if rec.Code != http.StatusCreated {
		t.Fatalf("upload 2 = %d", rec.Code)
	}

	var projects []Project
	_ = json.Unmarshal(do(t, mux, "GET", "/api/projects", "").Body.Bytes(), &projects)
	if len(projects) != 1 || projects[0].SnapshotCount != 2 {
		t.Fatalf("projects = %+v", projects)
	}
	if projects[0].Latest == nil || projects[0].Latest.Name != "方案 B" {
		t.Fatalf("latest = %+v", projects[0].Latest)
	}

	var snaps []SnapshotSummary
	_ = json.Unmarshal(do(t, mux, "GET", "/api/projects/1/snapshots", "").Body.Bytes(), &snaps)
	if len(snaps) != 2 || snaps[0].Name != "方案 B" {
		t.Fatalf("snapshots = %+v", snaps)
	}

	rec = do(t, mux, "GET", "/api/snapshots/1", "")
	var d SnapshotDetail
	_ = json.Unmarshal(rec.Body.Bytes(), &d)
	if d.Grade != "1級" || d.Cfr != 0.1805 || d.ProjectKey != "專案1" || len(d.Items) != 2 {
		t.Fatalf("detail = %+v", d)
	}
	if !strings.Contains(string(d.Inputs), `"aFu":336`) {
		t.Fatalf("inputs not stored as-is: %s", d.Inputs)
	}

	if rec := do(t, mux, "GET", "/api/snapshots/99", ""); rec.Code != http.StatusNotFound {
		t.Fatalf("missing snapshot = %d", rec.Code)
	}
}

func TestRejectsUnknownSchemaAndMissingFields(t *testing.T) {
	_, mux := newTestMux(t)

	rec := do(t, mux, "POST", "/api/snapshots", snapshotJSON("專案1", "方案 A", "1級", 0.18, 2))
	if rec.Code != http.StatusBadRequest || !strings.Contains(rec.Body.String(), "schemaVersion") {
		t.Fatalf("schema 2 = %d %s", rec.Code, rec.Body.String())
	}
	if rec := do(t, mux, "POST", "/api/snapshots", snapshotJSON("", "方案 A", "1級", 0.18, 1)); rec.Code != http.StatusBadRequest {
		t.Fatalf("empty key = %d", rec.Code)
	}
	if rec := do(t, mux, "POST", "/api/snapshots", "not json"); rec.Code != http.StatusBadRequest {
		t.Fatalf("bad json = %d", rec.Code)
	}
}

func TestSeedOnlyWhenEmpty(t *testing.T) {
	s, mux := newTestMux(t)
	dir := t.TempDir()
	_ = os.WriteFile(filepath.Join(dir, "a.json"), []byte(snapshotJSON("專案1", "範例", "1級", 0.18, 1)), 0o644)

	if n, err := s.SeedIfEmpty(dir); err != nil || n != 1 {
		t.Fatalf("seed = %d, %v", n, err)
	}
	if n, _ := s.SeedIfEmpty(dir); n != 0 {
		t.Fatalf("second seed should skip, got %d", n)
	}
	var projects []Project
	_ = json.Unmarshal(do(t, mux, "GET", "/api/projects", "").Body.Bytes(), &projects)
	if len(projects) != 1 {
		t.Fatalf("projects after seed = %d", len(projects))
	}
}

// 帶四階段與兩類非主結構的快照，partition 是內隔間碳排
func compareJSON(name string, partition, structure float64, excel string) string {
	b, _ := json.Marshal(map[string]any{
		"schemaVersion": 1,
		"plugin":        map[string]string{"version": "1.0.0", "manual": "LEBR-2025"},
		"project":       map[string]string{"key": "專案1", "title": "專案1"},
		"snapshot":      map[string]string{"name": name},
		"result": map[string]any{"grade": "1級", "cfr": 0.18, "eci": 355.7, "stages": map[string]any{
			"design": map[string]float64{"structure": structure, "nonStructural": 40000 + partition, "renew": 8000, "construction": 1000, "demolition": 6000},
		}},
		"items": []map[string]any{
			{"side": "design", "category": "cw", "categoryName": "不透光帷幕外牆及一般外牆", "carbon": 40000.0},
			{"side": "design", "category": "iw", "categoryName": "內隔間", "carbon": partition},
			{"side": "baseline", "category": "iw", "categoryName": "內隔間", "carbon": 99999.0},
		},
		"excel": excel,
	})
	return string(b)
}

func TestCompare(t *testing.T) {
	_, mux := newTestMux(t)
	do(t, mux, "POST", "/api/snapshots", compareJSON("方案 A：輕隔間", 1800, 55000, ""))
	do(t, mux, "POST", "/api/snapshots", compareJSON("方案 B：磚牆", 4600, 55000, ""))

	rec := do(t, mux, "GET", "/api/compare?ids=1,2", "")
	if rec.Code != http.StatusOK {
		t.Fatalf("compare = %d %s", rec.Code, rec.Body.String())
	}
	var c CompareResult
	_ = json.Unmarshal(rec.Body.Bytes(), &c)
	if len(c.Snapshots) != 2 || c.Snapshots[1].Name != "方案 B：磚牆" {
		t.Fatalf("snapshots = %+v", c.Snapshots)
	}
	total := c.Stages[len(c.Stages)-1]
	if total.Key != "total" || total.Diffs[1] != 2800 {
		t.Fatalf("total row = %+v", total)
	}
	// 只比設計案：內隔間差 2,800 排第一；外牆沒差，不標名次
	for _, row := range c.Categories {
		switch row.Key {
		case "iw":
			if row.Rank != 1 || row.Diffs[1] != 2800 || row.Values[0] != 1800 {
				t.Fatalf("iw row = %+v", row)
			}
		case "cw":
			if row.Rank != 0 {
				t.Fatalf("cw should not be ranked: %+v", row)
			}
		}
	}

	for _, q := range []string{"ids=1", "ids=1,1", "ids=1,2,3,4", "ids=a,b"} {
		if rec := do(t, mux, "GET", "/api/compare?"+q, ""); rec.Code != http.StatusBadRequest {
			t.Fatalf("%s = %d", q, rec.Code)
		}
	}
	if rec := do(t, mux, "GET", "/api/compare?ids=1,9", ""); rec.Code != http.StatusNotFound {
		t.Fatalf("missing id = %d", rec.Code)
	}
}

func TestExcelDownload(t *testing.T) {
	_, mux := newTestMux(t)
	do(t, mux, "POST", "/api/snapshots", compareJSON("有附檔", 1800, 55000, "UEsDBA==")) // "PK\x03\x04"
	do(t, mux, "POST", "/api/snapshots", compareJSON("沒附檔", 1800, 55000, ""))

	rec := do(t, mux, "GET", "/api/snapshots/1/excel", "")
	if rec.Code != http.StatusOK || rec.Body.String() != "PK\x03\x04" {
		t.Fatalf("excel = %d %q", rec.Code, rec.Body.String())
	}
	if cd := rec.Header().Get("Content-Disposition"); !strings.Contains(cd, "filename*=UTF-8''") {
		t.Fatalf("content-disposition = %s", cd)
	}
	if rec := do(t, mux, "GET", "/api/snapshots/2/excel", ""); rec.Code != http.StatusNotFound {
		t.Fatalf("no excel = %d", rec.Code)
	}
	if rec := do(t, mux, "POST", "/api/snapshots", compareJSON("壞檔", 1, 1, "@@@")); rec.Code != http.StatusBadRequest {
		t.Fatalf("bad base64 = %d", rec.Code)
	}
}

func TestAnalysisStoredAndOldDBMigrated(t *testing.T) {
	// 舊資料庫：snapshots 沒有 analysis_json 欄位
	path := filepath.Join(t.TempDir(), "old.db")
	old, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := old.Exec(`CREATE TABLE snapshots (id INTEGER PRIMARY KEY AUTOINCREMENT, project_id INTEGER NOT NULL,
		name TEXT NOT NULL, created_at TEXT NOT NULL, uploaded_at DATETIME NOT NULL DEFAULT (datetime('now')),
		plugin_version TEXT NOT NULL DEFAULT '', manual TEXT NOT NULL DEFAULT '', eec REAL NOT NULL DEFAULT 0,
		eecc REAL NOT NULL DEFAULT 0, cfr REAL NOT NULL DEFAULT 0, grade TEXT NOT NULL DEFAULT '',
		inputs_json TEXT NOT NULL DEFAULT '{}', shape_json TEXT NOT NULL DEFAULT 'null',
		result_json TEXT NOT NULL DEFAULT '{}', excel_blob BLOB)`); err != nil {
		t.Fatal(err)
	}
	old.Close()

	db, err := OpenDB(path)
	if err != nil {
		t.Fatalf("migrate old db: %v", err)
	}
	t.Cleanup(func() { db.Close() })
	s := NewServer(db)
	mux := http.NewServeMux()
	s.routes(mux)

	var up map[string]any
	_ = json.Unmarshal([]byte(snapshotJSON("專案1", "附分析", "1級", 0.1805, 1)), &up)
	up["analysis"] = map[string]any{"version": 1, "nextGrade": map[string]any{"grade": "1+級", "cfrNeeded": 0.2}}
	b, _ := json.Marshal(up)
	do(t, mux, "POST", "/api/snapshots", string(b))
	do(t, mux, "POST", "/api/snapshots", snapshotJSON("專案1", "舊外掛", "1級", 0.1805, 1))

	var withA, without SnapshotDetail
	_ = json.Unmarshal(do(t, mux, "GET", "/api/snapshots/1", "").Body.Bytes(), &withA)
	_ = json.Unmarshal(do(t, mux, "GET", "/api/snapshots/2", "").Body.Bytes(), &without)
	if !strings.Contains(string(withA.Analysis), `"1+級"`) {
		t.Fatalf("analysis = %s", withA.Analysis)
	}
	if string(without.Analysis) != "null" {
		t.Fatalf("old snapshot analysis = %s", without.Analysis)
	}
}

func listProjectsT(t *testing.T, mux *http.ServeMux) []Project {
	t.Helper()
	var projects []Project
	_ = json.Unmarshal(do(t, mux, "GET", "/api/projects", "").Body.Bytes(), &projects)
	return projects
}

// 刪快照：只刪那一筆與明細；刪到最後一筆時案件保留，回到「尚未評估」
func TestDeleteSnapshot(t *testing.T) {
	s, mux := newTestMux(t)
	var a, b struct{ ID int64 }
	_ = json.Unmarshal(do(t, mux, "POST", "/api/snapshots", snapshotJSON("專案1", "方案 A", "1級", 0.18, 1)).Body.Bytes(), &a)
	_ = json.Unmarshal(do(t, mux, "POST", "/api/snapshots", snapshotJSON("專案1", "方案 B", "2級", 0.13, 1)).Body.Bytes(), &b)

	if rec := do(t, mux, "DELETE", fmt.Sprintf("/api/snapshots/%d", a.ID), ""); rec.Code != http.StatusOK {
		t.Fatalf("delete A = %d %s", rec.Code, rec.Body.String())
	}
	if p := listProjectsT(t, mux); len(p) != 1 || p[0].SnapshotCount != 1 {
		t.Fatalf("projects after delete A = %+v", p)
	}
	var items int
	_ = s.DB.QueryRow(`SELECT COUNT(*) FROM snapshot_items WHERE snapshot_id = ?`, a.ID).Scan(&items)
	if items != 0 {
		t.Fatalf("items of deleted snapshot = %d", items)
	}

	if rec := do(t, mux, "DELETE", fmt.Sprintf("/api/snapshots/%d", b.ID), ""); rec.Code != http.StatusOK {
		t.Fatalf("delete B = %d %s", rec.Code, rec.Body.String())
	}
	if p := listProjectsT(t, mux); len(p) != 1 || p[0].SnapshotCount != 0 || p[0].Latest != nil {
		t.Fatalf("case should stay without snapshots, got %+v", p)
	}
	if rec := do(t, mux, "DELETE", fmt.Sprintf("/api/snapshots/%d", b.ID), ""); rec.Code != http.StatusNotFound {
		t.Fatalf("delete again = %d", rec.Code)
	}
}

// 刪案子：連同所有快照與明細，其他案子不受影響
func TestDeleteProject(t *testing.T) {
	s, mux := newTestMux(t)
	var a struct{ ProjectID int64 }
	_ = json.Unmarshal(do(t, mux, "POST", "/api/snapshots", snapshotJSON("專案1", "方案 A", "1級", 0.18, 1)).Body.Bytes(), &a)
	do(t, mux, "POST", "/api/snapshots", snapshotJSON("專案1", "方案 B", "2級", 0.13, 1))
	do(t, mux, "POST", "/api/snapshots", snapshotJSON("專案2", "方案 A", "1級", 0.18, 1))

	if rec := do(t, mux, "DELETE", fmt.Sprintf("/api/projects/%d", a.ProjectID), ""); rec.Code != http.StatusOK {
		t.Fatalf("delete project = %d %s", rec.Code, rec.Body.String())
	}
	if p := listProjectsT(t, mux); len(p) != 1 || p[0].Key != "專案2" {
		t.Fatalf("projects after delete = %+v", p)
	}
	var snaps, items int
	_ = s.DB.QueryRow(`SELECT COUNT(*) FROM snapshots`).Scan(&snaps)
	_ = s.DB.QueryRow(`SELECT COUNT(*) FROM snapshot_items`).Scan(&items)
	if snaps != 1 || items != 2 {
		t.Fatalf("left snapshots=%d items=%d, want 1 and 2", snaps, items)
	}
	if rec := do(t, mux, "DELETE", fmt.Sprintf("/api/projects/%d", a.ProjectID), ""); rec.Code != http.StatusNotFound {
		t.Fatalf("delete again = %d", rec.Code)
	}
}

// 範例只在第一次啟動匯入：使用者刪光案子後重啟，不會再冒出範例
func TestSeedNotRepeatedAfterDeleteAll(t *testing.T) {
	s, mux := newTestMux(t)
	dir := t.TempDir()
	_ = os.WriteFile(filepath.Join(dir, "a.json"), []byte(snapshotJSON("專案1", "範例", "1級", 0.18, 1)), 0o644)
	if n, err := s.SeedIfEmpty(dir); err != nil || n != 1 {
		t.Fatalf("seed = %d, %v", n, err)
	}
	p := listProjectsT(t, mux)
	do(t, mux, "DELETE", fmt.Sprintf("/api/projects/%d", p[0].ID), "")
	if n, err := s.SeedIfEmpty(dir); err != nil || n != 0 {
		t.Fatalf("seed after delete-all = %d, %v", n, err)
	}
}

// 開模型時登記案子：還沒快照也列出（排在有快照的後面）；重複登記只更新標題
func TestRegisterProject(t *testing.T) {
	_, mux := newTestMux(t)
	do(t, mux, "POST", "/api/snapshots", snapshotJSON("已評估", "方案 A", "1級", 0.18, 1))

	rec := do(t, mux, "POST", "/api/projects", `{"key":"SB_B_250212","title":"SB_B_250212"}`)
	if rec.Code != http.StatusOK {
		t.Fatalf("register = %d %s", rec.Code, rec.Body.String())
	}
	if rec := do(t, mux, "POST", "/api/projects", `{"key":"SB_B_250212","title":"新標題"}`); rec.Code != http.StatusOK {
		t.Fatalf("register again = %d", rec.Code)
	}
	p := listProjectsT(t, mux)
	if len(p) != 2 || p[0].Key != "已評估" || p[1].Key != "SB_B_250212" || p[1].Title != "新標題" || p[1].Latest != nil || p[1].SnapshotCount != 0 {
		t.Fatalf("projects = %+v", p)
	}
	if rec := do(t, mux, "POST", "/api/projects", `{"key":"  "}`); rec.Code != http.StatusBadRequest {
		t.Fatalf("empty key = %d", rec.Code)
	}
}

// 材料對照表裡的模型只在第一次啟動時轉成案子；刪掉後不再冒出來，已有的案子不重複建立
func TestImportMappingCasesOnce(t *testing.T) {
	dir := t.TempDir()
	blcc, mapping := filepath.Join(dir, "blcc.json"), filepath.Join(dir, "mapping.json")
	_ = os.WriteFile(blcc, []byte(testBlcc), 0o644)
	_ = os.WriteFile(mapping, []byte(testMapping), 0o644) // 專案1、SB
	store, err := NewMappingStore(blcc, mapping)
	if err != nil {
		t.Fatal(err)
	}
	s, mux := newTestMux(t)
	s.Mapping = store
	do(t, mux, "POST", "/api/snapshots", snapshotJSON("專案1", "方案 A", "1級", 0.18, 1))

	if n, err := s.ImportMappingCases(); err != nil || n != 1 {
		t.Fatalf("import = %d, %v（只有 SB 是新的）", n, err)
	}
	p := listProjectsT(t, mux)
	if len(p) != 2 || p[1].Key != "SB" {
		t.Fatalf("projects = %+v", p)
	}
	do(t, mux, "DELETE", fmt.Sprintf("/api/projects/%d", p[1].ID), "")
	if n, err := s.ImportMappingCases(); err != nil || n != 0 {
		t.Fatalf("second import = %d, %v", n, err)
	}
	if p := listProjectsT(t, mux); len(p) != 1 {
		t.Fatalf("deleted case came back: %+v", p)
	}
}
