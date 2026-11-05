package main

// 材料對照（原 MaterialMapping 工具，組員製作，整合進控制台）：
// 顯示「Revit 類型 → B-LCC 代碼」對照表，可用下拉選單修改 AI 的草稿，小計即時重算。
// 小計只是「新建碳排 × 面積」的材料試算，不是正式 LEBR 評估（那由外掛計算）。
// 對照表存在 MaterialMapping/data/mapping.json：AI agent 透過 Revit MCP 辨識後直接改這個檔，
// 網頁確認後再由 agent 寫回模型的 LEBR_BlccId（流程見 MaterialMapping/AGENTS.md）。

import (
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"sync"
	"time"
)

type BlccOption struct {
	ID         string  `json:"id"`
	Category   string  `json:"category"`
	Name       string  `json:"name"`
	New        float64 `json:"new"`
	BaselineID string  `json:"baselineId"`
}

type MappingItem struct {
	ElementID  int     `json:"elementId"`
	Project    string  `json:"project"` // 來源 Revit 專案名；不同檔案的 elementId 可能相同，一律連 project 比對
	TypeName   string  `json:"typeName"`
	Kind       string  `json:"kind"` // wall(CW+OW) / glazing(CW) / interior(IW) / window(W) / excluded
	BodyID     *string `json:"bodyId"`
	FinishID   *string `json:"finishId"`
	AreaM2     float64 `json:"areaM2"`
	Confidence string  `json:"confidence"` // confirmed / guess
	Note       string  `json:"note"`
}

type mappingFile struct {
	UpdatedAt string        `json:"updatedAt"`
	Items     []MappingItem `json:"items"`
}

// ResolvedMapping 是對照表一列加上 B-LCC 名稱與試算小計。
type ResolvedMapping struct {
	MappingItem
	BodyName   string  `json:"bodyName"`
	FinishName string  `json:"finishName"`
	UnitEC     float64 `json:"unitEC"` // kgCO2e/m²，本體 + 外裝的新建碳排
	Subtotal   float64 `json:"subtotal"`
}

// MappingStore 管 B-LCC 選項與對照表檔案。對照表檔案被 AI agent 改過（修改時間變了）就重新讀取，不必重啟後端。
type MappingStore struct {
	mu          sync.Mutex
	mappingPath string
	byID        map[string]BlccOption
	byCat       map[string][]BlccOption
	data        mappingFile
	loadedMod   time.Time
}

func NewMappingStore(blccPath, mappingPath string) (*MappingStore, error) {
	raw, err := os.ReadFile(blccPath)
	if err != nil {
		return nil, fmt.Errorf("讀不到 B-LCC 資料庫 %s：%w", blccPath, err)
	}
	var f struct {
		Items []BlccOption `json:"items"`
	}
	if err := json.Unmarshal(raw, &f); err != nil {
		return nil, fmt.Errorf("B-LCC 資料庫格式錯誤：%w", err)
	}
	s := &MappingStore{mappingPath: mappingPath, byID: map[string]BlccOption{}, byCat: map[string][]BlccOption{}}
	for _, it := range f.Items {
		s.byID[it.ID] = it
		s.byCat[it.Category] = append(s.byCat[it.Category], it)
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.refreshLocked(); err != nil {
		return nil, err
	}
	return s, nil
}

// refreshLocked 在檔案修改時間變了時重新讀取；檔案不存在視為空表。
func (s *MappingStore) refreshLocked() error {
	st, err := os.Stat(s.mappingPath)
	if errors.Is(err, os.ErrNotExist) {
		s.data, s.loadedMod = mappingFile{}, time.Time{}
		return nil
	}
	if err != nil {
		return err
	}
	if st.ModTime().Equal(s.loadedMod) {
		return nil
	}
	raw, err := os.ReadFile(s.mappingPath)
	if err != nil {
		return err
	}
	var f mappingFile
	if err := json.Unmarshal(raw, &f); err != nil {
		return fmt.Errorf("%s 格式錯誤：%w", filepath.Base(s.mappingPath), err)
	}
	s.data, s.loadedMod = f, st.ModTime()
	return nil
}

// saveLocked 先寫暫存檔再改名，避免寫到一半被 agent 或網頁讀到半個檔案。
func (s *MappingStore) saveLocked() error {
	s.data.UpdatedAt = time.Now().Format(time.RFC3339)
	raw, err := json.MarshalIndent(s.data, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(s.mappingPath), 0o755); err != nil {
		return err
	}
	tmp := s.mappingPath + ".tmp"
	if err := os.WriteFile(tmp, raw, 0o644); err != nil {
		return err
	}
	if err := os.Rename(tmp, s.mappingPath); err != nil {
		return err
	}
	if st, err := os.Stat(s.mappingPath); err == nil {
		s.loadedMod = st.ModTime()
	}
	return nil
}

func (s *MappingStore) resolve(it MappingItem) ResolvedMapping {
	r := ResolvedMapping{MappingItem: it}
	if it.BodyID != nil {
		if b, ok := s.byID[*it.BodyID]; ok {
			r.BodyName = b.Name
			r.UnitEC += b.New
		}
	}
	if it.FinishID != nil {
		if f, ok := s.byID[*it.FinishID]; ok {
			r.FinishName = f.Name
			r.UnitEC += f.New
		}
	}
	if it.Kind != "excluded" {
		r.Subtotal = r.UnitEC * it.AreaM2
	}
	return r
}

// GET /api/blcc?category=cw — 該類別的 B-LCC 選項（給下拉選單）；不帶 category 回傳全部，依類別分組。
func (s *Server) handleBlcc(w http.ResponseWriter, r *http.Request) {
	if s.Mapping == nil {
		writeErr(w, http.StatusServiceUnavailable, "材料對照未啟用（找不到 B-LCC 資料庫）")
		return
	}
	if cat := r.URL.Query().Get("category"); cat != "" {
		list := s.Mapping.byCat[cat]
		if list == nil {
			list = []BlccOption{}
		}
		writeJSON(w, http.StatusOK, list)
		return
	}
	writeJSON(w, http.StatusOK, s.Mapping.byCat)
}

// GET /api/mapping — 對照表（含試算小計）。
func (s *Server) handleMapping(w http.ResponseWriter, r *http.Request) {
	m := s.Mapping
	if m == nil {
		writeErr(w, http.StatusServiceUnavailable, "材料對照未啟用（找不到 B-LCC 資料庫）")
		return
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.refreshLocked(); err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	out := make([]ResolvedMapping, 0, len(m.data.Items))
	total := 0.0
	for _, it := range m.data.Items {
		ri := m.resolve(it)
		out = append(out, ri)
		total += ri.Subtotal
	}
	writeJSON(w, http.StatusOK, map[string]any{"items": out, "total": total, "updatedAt": m.data.UpdatedAt})
}

// PATCH /api/mapping/{elementId} — body: {project, bodyId?, finishId?, areaM2?}；代碼傳空字串代表清除。
// 改了本體或外裝代碼才標為「已確認」；只改面積不算確認過材料。
func (s *Server) handleMappingUpdate(w http.ResponseWriter, r *http.Request) {
	m := s.Mapping
	if m == nil {
		writeErr(w, http.StatusServiceUnavailable, "材料對照未啟用（找不到 B-LCC 資料庫）")
		return
	}
	elementID, err := strconv.Atoi(r.PathValue("elementId"))
	if err != nil {
		writeErr(w, http.StatusBadRequest, "elementId 須為數字")
		return
	}
	var body struct {
		Project  string   `json:"project"`
		BodyID   *string  `json:"bodyId"`
		FinishID *string  `json:"finishId"`
		AreaM2   *float64 `json:"areaM2"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeErr(w, http.StatusBadRequest, "JSON 無法解析："+err.Error())
		return
	}
	if body.AreaM2 != nil && *body.AreaM2 < 0 {
		writeErr(w, http.StatusBadRequest, "面積不可為負")
		return
	}
	setID := func(dst **string, v *string) error {
		if v == nil {
			return nil
		}
		if *v == "" {
			*dst = nil
			return nil
		}
		if _, ok := m.byID[*v]; !ok {
			return fmt.Errorf("B-LCC 代碼 %s 不存在", *v)
		}
		id := *v
		*dst = &id
		return nil
	}

	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.refreshLocked(); err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	for i := range m.data.Items {
		it := &m.data.Items[i]
		if it.ElementID != elementID || it.Project != body.Project {
			continue
		}
		updated := *it
		if err := setID(&updated.BodyID, body.BodyID); err != nil {
			writeErr(w, http.StatusBadRequest, err.Error())
			return
		}
		if err := setID(&updated.FinishID, body.FinishID); err != nil {
			writeErr(w, http.StatusBadRequest, err.Error())
			return
		}
		if body.AreaM2 != nil {
			updated.AreaM2 = *body.AreaM2
		}
		if body.BodyID != nil || body.FinishID != nil {
			updated.Confidence = "confirmed"
		}
		*it = updated
		if err := m.saveLocked(); err != nil {
			writeErr(w, 500, "存檔失敗："+err.Error())
			return
		}
		writeJSON(w, http.StatusOK, m.resolve(updated))
		return
	}
	writeErr(w, http.StatusNotFound, fmt.Sprintf("找不到專案「%s」的 elementId %d", body.Project, elementID))
}

func openMappingStore(blccPath, mappingPath string) *MappingStore {
	m, err := NewMappingStore(blccPath, mappingPath)
	if err != nil {
		log.Printf("材料對照未啟用：%v", err)
		return nil
	}
	log.Printf("材料對照：B-LCC %d 筆、對照表 %d 筆（%s）", len(m.byID), len(m.data.Items), mappingPath)
	return m
}
