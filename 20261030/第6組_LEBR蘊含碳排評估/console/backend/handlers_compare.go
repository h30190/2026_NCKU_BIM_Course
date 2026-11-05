package main

import (
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"net/http"
	"net/url"
	"sort"
	"strconv"
	"strings"
)

// stageKeys 是 result.stages.design 的五段，順序與網頁圖例一致。
var stageKeys = []string{"structure", "nonStructural", "renew", "construction", "demolition"}

// CompareResult 是 2 ~ 3 個快照的並排資料（控制台計畫書 W5）。
// 差值一律是「該快照 − 第一個快照」，只做減法，不重算碳排。
type CompareResult struct {
	Snapshots  []CompareSnapshot `json:"snapshots"`
	Stages     []CompareRow      `json:"stages"`
	Categories []CompareRow      `json:"categories"`
}

type CompareSnapshot struct {
	SnapshotSummary
	ProjectKey   string  `json:"projectKey"`
	ProjectTitle string  `json:"projectTitle"`
	Eci          float64 `json:"eci"`
}

// CompareRow 是比較表的一列：各快照的值、與第一個快照的差值、最大最小差距。
// Rank 1 ~ 3 代表非主結構類別中差距最大的前三項，0 代表不在前三。
type CompareRow struct {
	Key    string    `json:"key"`
	Label  string    `json:"label"`
	Values []float64 `json:"values"`
	Diffs  []float64 `json:"diffs"`
	Spread float64   `json:"spread"`
	Rank   int       `json:"rank"`
}

// GET /api/compare?ids=1,2,3
func (s *Server) handleCompare(w http.ResponseWriter, r *http.Request) {
	ids, err := parseIDs(r.URL.Query().Get("ids"))
	if err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}

	details := make([]*SnapshotDetail, 0, len(ids))
	for _, id := range ids {
		d, err := s.loadSnapshot(id)
		if errors.Is(err, sql.ErrNoRows) {
			writeErr(w, http.StatusNotFound, fmt.Sprintf("找不到快照 %d", id))
			return
		}
		if err != nil {
			writeErr(w, 500, err.Error())
			return
		}
		details = append(details, d)
	}
	out, err := buildCompare(details)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, out)
}

func parseIDs(q string) ([]int64, error) {
	var ids []int64
	seen := map[int64]bool{}
	for _, part := range strings.Split(q, ",") {
		part = strings.TrimSpace(part)
		if part == "" {
			continue
		}
		id, err := strconv.ParseInt(part, 10, 64)
		if err != nil {
			return nil, fmt.Errorf("ids 須為以逗號分隔的數字")
		}
		if !seen[id] {
			seen[id] = true
			ids = append(ids, id)
		}
	}
	if len(ids) < 2 || len(ids) > 3 {
		return nil, fmt.Errorf("請選 2 ~ 3 個不同的快照比較（目前 %d 個）", len(ids))
	}
	return ids, nil
}

func buildCompare(details []*SnapshotDetail) (*CompareResult, error) {
	out := &CompareResult{}
	stageVals := make([]map[string]float64, len(details))

	for i, d := range details {
		var res struct {
			Eci    float64 `json:"eci"`
			Stages struct {
				Design map[string]float64 `json:"design"`
			} `json:"stages"`
		}
		if err := json.Unmarshal(d.Result, &res); err != nil {
			return nil, fmt.Errorf("快照 %d 的 result 無法解析：%w", d.ID, err)
		}
		stageVals[i] = res.Stages.Design
		out.Snapshots = append(out.Snapshots, CompareSnapshot{
			SnapshotSummary: d.SnapshotSummary, ProjectKey: d.ProjectKey, ProjectTitle: d.ProjectTitle, Eci: res.Eci,
		})
	}

	// 設計案四階段（五段）＋合計
	totals := make([]float64, len(details))
	for _, k := range stageKeys {
		vals := make([]float64, len(details))
		for i := range details {
			vals[i] = stageVals[i][k]
			totals[i] += vals[i]
		}
		out.Stages = append(out.Stages, newRow(k, "", vals))
	}
	out.Stages = append(out.Stages, newRow("total", "合計", totals))

	// 非主結構依類別加總設計案碳排（新建 + 更新）
	type cat struct{ code, name string }
	var order []cat
	sums := map[string][]float64{}
	for i, d := range details {
		for _, it := range d.Items {
			if it.Side != "design" {
				continue
			}
			if _, ok := sums[it.Category]; !ok {
				sums[it.Category] = make([]float64, len(details))
				order = append(order, cat{it.Category, it.CategoryName})
			}
			sums[it.Category][i] += it.Carbon
		}
	}
	for _, c := range order {
		out.Categories = append(out.Categories, newRow(c.code, c.name, sums[c.code]))
	}

	// 差距最大的前三個類別（差距為 0 的不標）
	idx := make([]int, len(out.Categories))
	for i := range idx {
		idx[i] = i
	}
	sort.SliceStable(idx, func(a, b int) bool { return out.Categories[idx[a]].Spread > out.Categories[idx[b]].Spread })
	for rank, i := range idx {
		if rank >= 3 || out.Categories[i].Spread < 0.005 {
			break
		}
		out.Categories[i].Rank = rank + 1
	}
	return out, nil
}

func newRow(key, label string, vals []float64) CompareRow {
	row := CompareRow{Key: key, Label: label, Values: vals, Diffs: make([]float64, len(vals))}
	lo, hi := math.Inf(1), math.Inf(-1)
	for i, v := range vals {
		row.Diffs[i] = v - vals[0]
		lo, hi = math.Min(lo, v), math.Max(hi, v)
	}
	row.Spread = hi - lo
	return row
}

// GET /api/snapshots/{id}/excel — 下載外掛上傳時附上的 Excel 計算書（W7）。
func (s *Server) handleDownloadExcel(w http.ResponseWriter, r *http.Request) {
	id, ok := pathID(w, r)
	if !ok {
		return
	}
	var blob []byte
	var name, title string
	err := s.DB.QueryRow(`SELECT s.excel_blob, s.name, p.title FROM snapshots s JOIN projects p ON p.id = s.project_id WHERE s.id = ?`, id).
		Scan(&blob, &name, &title)
	if errors.Is(err, sql.ErrNoRows) {
		writeErr(w, http.StatusNotFound, "找不到這筆快照")
		return
	}
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	if len(blob) == 0 {
		writeErr(w, http.StatusNotFound, "這筆快照上傳時沒有附 Excel 計算書")
		return
	}
	filename := safeFileName(fmt.Sprintf("%s-%s-計算書.xlsx", title, name))
	w.Header().Set("Content-Type", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
	// filename* 讓瀏覽器正確顯示中文檔名；filename 留 ASCII 備援
	w.Header().Set("Content-Disposition", fmt.Sprintf(`attachment; filename="snapshot-%d.xlsx"; filename*=UTF-8''%s`, id, url.PathEscape(filename)))
	w.Header().Set("Content-Length", strconv.Itoa(len(blob)))
	_, _ = w.Write(blob)
}

func safeFileName(s string) string {
	return strings.Map(func(r rune) rune {
		if strings.ContainsRune(`\/:*?"<>|`, r) || r < 32 {
			return '_'
		}
		return r
	}, s)
}
