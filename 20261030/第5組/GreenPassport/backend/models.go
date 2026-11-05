package main

// ElementSnapshot 對應一筆元件在某個版本的狀態。
// 從 Revit 外掛送上來的資料就長這個樣子（JSON 欄位名用 camelCase）。
type ElementSnapshot struct {
	UniqueID  string  `json:"uniqueId"`
	ElementID string  `json:"elementId"`
	TypeName  string  `json:"typeName"`
	Material  string  `json:"material"`
	Quantity  float64 `json:"quantity"`
	PositionX float64 `json:"positionX"`
	PositionY float64 `json:"positionY"`
	PositionZ float64 `json:"positionZ"`
	LevelName string  `json:"levelName"`
}

// SnapshotRequest 是 POST /snapshot 的 request body。
type SnapshotRequest struct {
	Label     string             `json:"label"`
	ModelName string             `json:"modelName"`
	Elements  []ElementSnapshot  `json:"elements"`
}

// ModelVersion 對應 model_versions 表的一列。
type ModelVersion struct {
	ID           int64  `json:"id"`
	Label        string `json:"label"`
	ModelName    string `json:"modelName"`
	CreatedAt    string `json:"createdAt"`
	ElementCount int    `json:"elementCount"`
}

// ChangedField 記錄「修改」類型的元件，單一欄位的新舊值。
type ChangedField struct {
	Field    string `json:"field"`
	OldValue string `json:"oldValue"`
	NewValue string `json:"newValue"`
}

// ChangeEvent 是 Diff 結果裡的一筆元件異動紀錄。
// ChangeType 是 "added" / "removed" / "modified" 三選一。
// LastSnapshot 帶著這個元件最後已知的欄位值，Case B（歷史卡片）直接拿這個顯示，
// 不用再額外查一次資料庫。
type ChangeEvent struct {
	UniqueID     string          `json:"uniqueId"`
	ChangeType   string          `json:"changeType"`
	ChangedFields []ChangedField `json:"changedFields,omitempty"`
	LastSnapshot ElementSnapshot `json:"lastSnapshot"`
}

// DiffResult 是 GET /diff 的回傳格式。
type DiffResult struct {
	FromVersionID int64         `json:"fromVersionId"`
	ToVersionID   int64         `json:"toVersionId"`
	Changes       []ChangeEvent `json:"changes"`
}
