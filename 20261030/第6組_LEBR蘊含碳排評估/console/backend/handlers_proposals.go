package main

// 減碳建議（AI 分析計畫書 6 節）：AI agent 用 lebr-snapshot scenario 試算後，把建議寫進 proposals.json；
// 網頁選定一份或多份互不衝突的建議（chosen），外掛「套用建議」一次取回、合併寫回 Revit 後逐份回報已套用（applied）；
// 套用過的可再選定重新套用，也可刪除。衝突規則見 proposal_conflicts.go。
// 後端只存狀態，不計算碳排，也從不呼叫 Revit。

import (
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// ProposalChange 與外掛 ScenarioChange 相同：把代碼 From 換成 To（同類別）。
type ProposalChange struct {
	From       string  `json:"from"`
	To         string  `json:"to"`
	TypeName   *string `json:"typeName,omitempty"` // 只換這個 Revit 類型；空 = 所有用 From 的類型
	DropFinish bool    `json:"dropFinish"`         // 換成帷幕牆時一併拿掉同類型的外裝（OW）
}

type ProposalResult struct {
	Cfr   float64  `json:"cfr"`
	Grade string   `json:"grade"`
	Eec   *float64 `json:"eec,omitempty"`
}

type Proposal struct {
	ID        string           `json:"id"`
	Project   string           `json:"project"` // 案子 key（模型檔名）
	Title     string           `json:"title"`
	Reason    string           `json:"reason,omitempty"`
	Changes   []ProposalChange `json:"changes"`
	Expected  *ProposalResult  `json:"expected,omitempty"` // AI 試算的預期結果
	Status    string           `json:"status"`             // proposed / chosen / applied
	CreatedAt string           `json:"createdAt,omitempty"`
	ChosenAt  string           `json:"chosenAt,omitempty"`
	Applied   *ProposalResult  `json:"applied,omitempty"` // 外掛套用後重算的結果
	AppliedAt string           `json:"appliedAt,omitempty"`
	// 只在列表回應時計算，不存進檔案：與同案件哪些建議不能同時選定
	Conflicts []ProposalConflict `json:"conflicts,omitempty"`
}

type proposalFile struct {
	UpdatedAt string     `json:"updatedAt"`
	Proposals []Proposal `json:"proposals"`
}

// ProposalStore 與 MappingStore 相同做法：agent 改過檔案（修改時間變了）就重新讀取。
type ProposalStore struct {
	mu        sync.Mutex
	path      string
	data      proposalFile
	loadedMod time.Time
}

func NewProposalStore(path string) *ProposalStore {
	s := &ProposalStore{path: path}
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.refreshLocked(); err != nil {
		log.Printf("減碳建議：%v（先當作沒有建議）", err)
	} else {
		log.Printf("減碳建議：%d 筆（%s）", len(s.data.Proposals), path)
	}
	return s
}

func (s *ProposalStore) refreshLocked() error {
	st, err := os.Stat(s.path)
	if errors.Is(err, os.ErrNotExist) {
		s.data, s.loadedMod = proposalFile{}, time.Time{}
		return nil
	}
	if err != nil {
		return err
	}
	if st.ModTime().Equal(s.loadedMod) {
		return nil
	}
	raw, err := os.ReadFile(s.path)
	if err != nil {
		return err
	}
	var f proposalFile
	if err := json.Unmarshal(raw, &f); err != nil {
		return fmt.Errorf("%s 格式錯誤：%w", filepath.Base(s.path), err)
	}
	for i := range f.Proposals {
		if f.Proposals[i].Status == "" {
			f.Proposals[i].Status = "proposed"
		}
	}
	s.data, s.loadedMod = f, st.ModTime()
	return nil
}

func (s *ProposalStore) saveLocked() error {
	s.data.UpdatedAt = time.Now().Format(time.RFC3339)
	raw, err := json.MarshalIndent(s.data, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(s.path), 0o755); err != nil {
		return err
	}
	tmp := s.path + ".tmp"
	if err := os.WriteFile(tmp, raw, 0o644); err != nil {
		return err
	}
	if err := os.Rename(tmp, s.path); err != nil {
		return err
	}
	if st, err := os.Stat(s.path); err == nil {
		s.loadedMod = st.ModTime()
	}
	return nil
}

func (s *ProposalStore) findLocked(id string) *Proposal {
	for i := range s.data.Proposals {
		if s.data.Proposals[i].ID == id {
			return &s.data.Proposals[i]
		}
	}
	return nil
}

// lockFresh 上鎖並重讀檔案；失敗時已回應錯誤並解鎖。
func (s *Server) lockProposals(w http.ResponseWriter) (*ProposalStore, bool) {
	p := s.Proposals
	if p == nil {
		writeErr(w, http.StatusServiceUnavailable, "減碳建議未啟用")
		return nil, false
	}
	p.mu.Lock()
	if err := p.refreshLocked(); err != nil {
		p.mu.Unlock()
		writeErr(w, 500, err.Error())
		return nil, false
	}
	return p, true
}

// GET /api/proposals?project= — 該案子的建議（不帶 project 回傳全部），依檔案順序；
// 每份附上與同案件哪些建議衝突（conflicts），網頁據此標示不能同時選定的組合。
func (s *Server) handleListProposals(w http.ResponseWriter, r *http.Request) {
	p, ok := s.lockProposals(w)
	if !ok {
		return
	}
	defer p.mu.Unlock()
	project := r.URL.Query().Get("project")
	out := []Proposal{}
	for i := range p.data.Proposals {
		x := p.data.Proposals[i]
		if project == "" || x.Project == project {
			x.Conflicts = conflictsOf(&p.data.Proposals[i], p.data.Proposals)
			out = append(out, x)
		}
	}
	writeJSON(w, http.StatusOK, out)
}

// GET /api/proposals/chosen?project= — 外掛取回目前選定的所有建議（依檔案順序），一次合併套用；沒有時回空陣列。
func (s *Server) handleChosenProposal(w http.ResponseWriter, r *http.Request) {
	project := r.URL.Query().Get("project")
	if project == "" {
		writeErr(w, http.StatusBadRequest, "project 必填")
		return
	}
	p, ok := s.lockProposals(w)
	if !ok {
		return
	}
	defer p.mu.Unlock()
	out := []Proposal{}
	for _, x := range p.data.Proposals {
		if x.Project == project && x.Status == "chosen" {
			out = append(out, x)
		}
	}
	writeJSON(w, http.StatusOK, out)
}

// unchosenStatus：取消選定後回到的狀態——套用過的回 applied（保留套用紀錄），沒套用過的回 proposed。
func unchosenStatus(x *Proposal) string {
	if x.Applied != nil {
		return "applied"
	}
	return "proposed"
}

// PATCH /api/proposals/{id} — body: {status: "chosen" | "proposed"}。"proposed" 代表取消選定。
// 同一案子可以選定多份，但不能和已選定的衝突（回 409 並說明原因）。套用過的也能再選定（在 Revit 重新套用一次）。
func (s *Server) handleChooseProposal(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Status string `json:"status"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeErr(w, http.StatusBadRequest, "JSON 無法解析："+err.Error())
		return
	}
	if body.Status != "chosen" && body.Status != "proposed" {
		writeErr(w, http.StatusBadRequest, "status 只能是 chosen 或 proposed")
		return
	}
	p, ok := s.lockProposals(w)
	if !ok {
		return
	}
	defer p.mu.Unlock()
	x := p.findLocked(r.PathValue("id"))
	if x == nil {
		writeErr(w, http.StatusNotFound, "找不到這份建議")
		return
	}
	if body.Status == "chosen" && x.Status != "chosen" {
		for i := range p.data.Proposals {
			o := &p.data.Proposals[i]
			if o.Status != "chosen" {
				continue
			}
			if reason := proposalConflict(x, o); reason != "" {
				writeErr(w, http.StatusConflict, fmt.Sprintf("和已選定的「%s」衝突：%s。請先取消選定那一份。", o.Title, reason))
				return
			}
		}
		x.Status, x.ChosenAt = "chosen", time.Now().Format(time.RFC3339)
	} else if body.Status == "proposed" && x.Status == "chosen" {
		x.Status, x.ChosenAt = unchosenStatus(x), ""
	}
	if err := p.saveLocked(); err != nil {
		writeErr(w, 500, "存檔失敗："+err.Error())
		return
	}
	writeJSON(w, http.StatusOK, x)
}

// DELETE /api/proposals/{id} — 從 proposals.json 移除這份建議。只刪紀錄：已寫回模型的參數與材料對照表不會還原。
func (s *Server) handleDeleteProposal(w http.ResponseWriter, r *http.Request) {
	p, ok := s.lockProposals(w)
	if !ok {
		return
	}
	defer p.mu.Unlock()
	id := r.PathValue("id")
	list := p.data.Proposals
	for i := range list {
		if list[i].ID == id {
			p.data.Proposals = append(list[:i:i], list[i+1:]...)
			if err := p.saveLocked(); err != nil {
				writeErr(w, 500, "存檔失敗："+err.Error())
				return
			}
			writeJSON(w, http.StatusOK, map[string]string{"id": id})
			return
		}
	}
	writeErr(w, http.StatusNotFound, "找不到這份建議")
}

// PATCH /api/proposals/{id}/applied — 外掛寫回 Revit 後回報，body: {applied: {cfr, grade, eec}}。
// 只有選定中的建議能回報；同時把材料對照表裡同案子、同代碼的類型改成新代碼，網頁與模型一致。
func (s *Server) handleProposalApplied(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Applied *ProposalResult `json:"applied"`
	}
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeErr(w, http.StatusBadRequest, "JSON 無法解析："+err.Error())
		return
	}
	if body.Applied == nil || body.Applied.Grade == "" {
		writeErr(w, http.StatusBadRequest, "applied.grade 必填")
		return
	}
	p, ok := s.lockProposals(w)
	if !ok {
		return
	}
	defer p.mu.Unlock()
	x := p.findLocked(r.PathValue("id"))
	if x == nil {
		writeErr(w, http.StatusNotFound, "找不到這份建議")
		return
	}
	if x.Status != "chosen" {
		writeErr(w, http.StatusConflict, fmt.Sprintf("這份建議目前是 %s，只有選定中的建議能回報套用", x.Status))
		return
	}
	x.Status, x.Applied, x.AppliedAt = "applied", body.Applied, time.Now().Format(time.RFC3339)
	if err := p.saveLocked(); err != nil {
		writeErr(w, 500, "存檔失敗："+err.Error())
		return
	}

	updated := 0
	if s.Mapping != nil {
		n, err := s.Mapping.applyChanges(x.Project, x.Changes)
		if err != nil {
			log.Printf("建議 %s 已套用，但更新材料對照表失敗：%v", x.ID, err)
		}
		updated = n
	}
	writeJSON(w, http.StatusOK, map[string]any{"proposal": x, "mappingUpdated": updated})
}

// applyChanges 把對照表中同案子的本體／外裝代碼 From 改成 To；DropFinish 時清掉外裝。回傳改了幾列。
func (m *MappingStore) applyChanges(project string, changes []ProposalChange) (int, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.refreshLocked(); err != nil {
		return 0, err
	}
	n := 0
	for i := range m.data.Items {
		it := &m.data.Items[i]
		if it.Project != project {
			continue
		}
		changed := false
		for _, c := range changes {
			if c.TypeName != nil && *c.TypeName != it.TypeName {
				continue
			}
			to := c.To
			if it.BodyID != nil && *it.BodyID == c.From {
				it.BodyID, changed = &to, true
				if c.DropFinish {
					it.FinishID = nil
				}
			} else if it.FinishID != nil && *it.FinishID == c.From {
				it.FinishID, changed = &to, true
			}
		}
		if changed {
			it.Confidence = "confirmed"
			n++
		}
	}
	if n == 0 {
		return 0, nil
	}
	return n, m.saveLocked()
}
