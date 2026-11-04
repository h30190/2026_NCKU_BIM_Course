package main

import "encoding/json"

// SupportedSchemaVersion 是後端認得的快照格式版本；不認得就回 400（控制台計畫書 5.4 節）。
const SupportedSchemaVersion = 1

// SnapshotUpload 是外掛 POST 的快照（控制台計畫書 5.1 節）。
// inputs、shape、result 原樣存成 JSON，後端只抽出列表與排序要用的幾個數字，不自己算碳排。
type SnapshotUpload struct {
	SchemaVersion int `json:"schemaVersion"`
	Plugin        struct {
		Version string `json:"version"`
		Manual  string `json:"manual"`
	} `json:"plugin"`
	Project struct {
		Key   string `json:"key"`
		Title string `json:"title"`
	} `json:"project"`
	Snapshot struct {
		Name      string `json:"name"`
		CreatedAt string `json:"createdAt"`
	} `json:"snapshot"`
	Inputs json.RawMessage `json:"inputs"`
	Shape  json.RawMessage `json:"shape"`
	Result json.RawMessage `json:"result"`
	Items  []Item          `json:"items"`
	Excel  string          `json:"excel"` // base64 .xlsx，可空
	// Analysis 是外掛算好的佔比、改善潛力、距下一級（AI 分析計畫書 5.1 節），原樣存；舊快照沒有。
	Analysis json.RawMessage `json:"analysis"`
}

// resultHead 是從 result 抽出的獨立欄位。
type resultHead struct {
	Eec   float64 `json:"eec"`
	Eecc  float64 `json:"eecc"`
	Cfr   float64 `json:"cfr"`
	Grade string  `json:"grade"`
}

// Item 是非主結構明細一列（side = design / baseline）。
type Item struct {
	Side         string          `json:"side"`
	BlccID       string          `json:"blccId"`
	Category     string          `json:"category"`
	CategoryName string          `json:"categoryName"`
	Name         string          `json:"name"`
	Area         float64         `json:"area"`
	NewEf        float64         `json:"newEf"`
	RenewEf      float64         `json:"renewEf"`
	NewCarbon    float64         `json:"newCarbon"`
	RenewCarbon  float64         `json:"renewCarbon"`
	Carbon       float64         `json:"carbon"`
	Source       string          `json:"source"`
	Elements     json.RawMessage `json:"elements"`
}

// SnapshotSummary 是列表用的快照摘要（不含大型 JSON）。
type SnapshotSummary struct {
	ID            int64   `json:"id"`
	ProjectID     int64   `json:"projectId"`
	Name          string  `json:"name"`
	CreatedAt     string  `json:"createdAt"`
	PluginVersion string  `json:"pluginVersion"`
	Manual        string  `json:"manual"`
	Eec           float64 `json:"eec"`
	Eecc          float64 `json:"eecc"`
	Cfr           float64 `json:"cfr"`
	Grade         string  `json:"grade"`
	HasExcel      bool    `json:"hasExcel"`
}

// SnapshotDetail 是單一快照的完整內容。
type SnapshotDetail struct {
	SnapshotSummary
	ProjectKey   string          `json:"projectKey"`
	ProjectTitle string          `json:"projectTitle"`
	Inputs       json.RawMessage `json:"inputs"`
	Shape        json.RawMessage `json:"shape"`
	Result       json.RawMessage `json:"result"`
	Analysis     json.RawMessage `json:"analysis"` // 舊快照為 null
	Items        []Item          `json:"items"`
}

// Project 是案子列表的一列，附最新快照。
type Project struct {
	ID            int64            `json:"id"`
	Key           string           `json:"key"`
	Title         string           `json:"title"`
	SnapshotCount int              `json:"snapshotCount"`
	Latest        *SnapshotSummary `json:"latest"`
}
