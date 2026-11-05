package main

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"net/http"
	"sort"
	"strconv"
	"strings"
	"time"
)

// Door width and height are always matched with the course-defined ±5 mm rule.
// Keeping this server-side prevents a UI/client from silently relaxing it.
const matchingDimensionToleranceMm = 5.0

// POST /passports — A-model batch capture. Existing source identities are not duplicated.
func (s *Server) handleCreatePassport(w http.ResponseWriter, r *http.Request) {
	var p Passport
	if err := json.NewDecoder(r.Body).Decode(&p); err != nil {
		writeErr(w, http.StatusBadRequest, "JSON 解析失敗: "+err.Error())
		return
	}
	if p.SourceUniqueID == "" || p.SourceModel == "" {
		writeErr(w, http.StatusBadRequest, "sourceModel 與 sourceUniqueId 不能是空的")
		return
	}
	var existingID int64
	if err := s.DB.QueryRow(`SELECT id FROM passports WHERE source_model=? AND source_unique_id=? ORDER BY id LIMIT 1`, p.SourceModel, p.SourceUniqueID).Scan(&existingID); err == nil {
		writeErr(w, http.StatusConflict, fmt.Sprintf("此來源元件已有護照（資料庫 id %d），請從護照清單檢核更新", existingID))
		return
	} else if err != sql.ErrNoRows {
		writeErr(w, 500, err.Error())
		return
	}
	applyPassportDefaults(&p)
	recoverLeafThickness(&p)

	if err := validateReview(p.ReuseStatus, p.ReviewNote, p.InspectedBy, p.DimensionDefinition, p.WidthMm, p.HeightMm); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	if err := validateRequiredCore(p); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	p.Status = supplyStatus(p.ReuseStatus, p.Quantity)
	now := time.Now().UTC().Format(time.RFC3339)
	temporaryID := fmt.Sprintf("PENDING-%d", time.Now().UnixNano())
	rawJSON := mustJSON(p.RawParameters, "[]")
	sourcesJSON := mustJSON(p.FieldSources, "{}")
	warningsJSON := mustJSON(p.MappingWarnings, "[]")

	res, err := s.DB.Exec(`INSERT INTO passports
		(passport_id, category, source_model, source_unique_id, source_element_id, family, type_name,
		 width_mm, height_mm, dimension_definition, leaf_thickness_mm, frame_dimensions, operation_type, swing_handing, use_type,
		 clear_width_mm, leaf_count, material, fire_rating, fire_minutes, fire_certificate_reference, fire_certificate_verified,
		 condition_text, condition_grade, reuse_status, review_note, quantity, price_ntd,
		 placement_requirements, inspected_by, inspected_on, status, photo, revision,
		 raw_parameters_json, field_sources_json, mapping_warnings_json, created_at, updated_at)
		VALUES (?,?,?,?,?,?,?, ?,?,?,?,?,?,?,?, ?,?,?,?,?,?,?, ?,?,?,?,?,?, ?,?,?,?,?,?, ?,?,?,?,?)`,
		temporaryID, p.Category, p.SourceModel, p.SourceUniqueID, p.SourceElementID, p.Family, p.TypeName,
		p.WidthMm, p.HeightMm, p.DimensionDefinition, p.LeafThicknessMm, p.FrameDimensions, p.OperationType, p.SwingHanding, p.UseType,
		p.ClearWidthMm, p.LeafCount, p.Material, p.FireRating, p.FireMinutes, p.FireCertificateReference, boolInt(p.FireCertificateVerified),
		p.Condition, p.ConditionGrade, p.ReuseStatus, p.ReviewNote, p.Quantity, p.PriceNtd,
		p.PlacementRequirements, p.InspectedBy, p.InspectedOn, p.Status, p.Photo, 1,
		rawJSON, sourcesJSON, warningsJSON, now, now)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	id, _ := res.LastInsertId()
	passportID := fmt.Sprintf("GP-D-%03d", id)
	if _, err := s.DB.Exec(`UPDATE passports SET passport_id=? WHERE id=?`, passportID, id); err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	logPassportEvent(s.DB, id, "capture", p.InspectedBy, map[string]any{"sourceModel": p.SourceModel, "sourceUniqueId": p.SourceUniqueID})
	out, err := getPassportByID(s.DB, id)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusCreated, out)
}

func applyPassportDefaults(p *Passport) {
	if p.LeafCount <= 0 {
		p.LeafCount = 1
	}
	if p.Quantity <= 0 {
		p.Quantity = 1
	}
	if !validReuseStatus[p.ReuseStatus] {
		p.ReuseStatus = "Pending"
	}
	if p.DimensionDefinition == "" {
		p.DimensionDefinition = "nominal"
	}
	if p.FieldSources == nil {
		p.FieldSources = map[string]FieldSource{}
	}
	if p.RawParameters == nil {
		p.RawParameters = []RawParameter{}
	}
	if p.MappingWarnings == nil {
		p.MappingWarnings = []string{}
	}
}

func validateReview(status, note, inspector, definition string, width, height float64) error {
	if !validReuseStatus[status] {
		return fmt.Errorf("再利用資格須為 Pending、Approved、Repair Required 或 Rejected")
	}
	if status == "Approved" {
		if strings.TrimSpace(note) == "" || strings.TrimSpace(inspector) == "" || width <= 0 || height <= 0 || strings.TrimSpace(definition) == "" {
			return fmt.Errorf("Approved 需要審核說明、檢核人、有效寬高及明確尺寸定義")
		}
	}
	if (status == "Repair Required" || status == "Rejected") && strings.TrimSpace(note) == "" {
		return fmt.Errorf("修復或排除狀態必須填寫審核原因")
	}
	return nil
}

// validateRequiredCore makes the yellow "required" fields a real approval gate,
// while still allowing incomplete captures to be stored as Pending.
func validateRequiredCore(p Passport) error {
	if p.ReuseStatus != "Approved" {
		return nil
	}
	missing := []string{}
	if p.WidthMm <= 0 {
		missing = append(missing, "門寬")
	}
	if p.HeightMm <= 0 {
		missing = append(missing, "門高")
	}
	if p.LeafThicknessMm <= 0 {
		missing = append(missing, "門扇厚度")
	}
	if strings.TrimSpace(p.OperationType) == "" {
		missing = append(missing, "開啟方式")
	}
	if strings.TrimSpace(p.SwingHanding) == "" {
		missing = append(missing, "開門方向")
	}
	if strings.TrimSpace(p.UseType) == "" {
		missing = append(missing, "室內外用途")
	}
	if strings.TrimSpace(p.Material) == "" {
		missing = append(missing, "材質")
	}
	if p.ClearWidthMm <= 0 {
		missing = append(missing, "有效通行淨寬")
	}
	if len(missing) > 0 {
		return fmt.Errorf("Approved 必填欄位未完成：%s", strings.Join(missing, "、"))
	}
	return nil
}

func supplyStatus(reuseStatus string, quantity int) string {
	if reuseStatus == "Approved" && quantity > 0 {
		return "Available"
	}
	return "Unavailable"
}

// GET /passports is the read-only dashboard API and the Revit list source.
func (s *Server) handleListPassports(w http.ResponseWriter, r *http.Request) {
	query := `SELECT ` + passportColumns + ` FROM passports`
	var args []any
	if status := r.URL.Query().Get("status"); status != "" {
		if !validPassportStatus[status] {
			writeErr(w, http.StatusBadRequest, "status 不正確")
			return
		}
		query += " WHERE status=?"
		args = append(args, status)
	}
	query += " ORDER BY id ASC"
	rows, err := s.DB.Query(query, args...)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer rows.Close()
	out := []Passport{}
	for rows.Next() {
		passport, err := scanPassport(rows)
		if err != nil {
			writeErr(w, 500, err.Error())
			return
		}
		out = append(out, passport)
	}
	writeJSON(w, http.StatusOK, out)
}

// POST /passports/match compares one designer-confirmed B door against approved supply.
func (s *Server) handleMatchPassports(w http.ResponseWriter, r *http.Request) {
	var req MatchRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeErr(w, http.StatusBadRequest, "JSON 解析失敗: "+err.Error())
		return
	}
	req.ToleranceMm = matchingDimensionToleranceMm
	rows, err := s.DB.Query(`SELECT ` + passportColumns + ` FROM passports WHERE reuse_status='Approved' AND status='Available' AND quantity>0`)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer rows.Close()
	results := []MatchResult{}
	for rows.Next() {
		passport, err := scanPassport(rows)
		if err != nil {
			writeErr(w, 500, err.Error())
			return
		}
		results = append(results, evaluateMatch(passport, req))
	}
	sort.SliceStable(results, func(i, j int) bool {
		rank := map[string]int{"Matched": 0, "Pending Review": 1, "Not Matched": 2}
		if rank[results[i].Result] != rank[results[j].Result] {
			return rank[results[i].Result] < rank[results[j].Result]
		}
		if results[i].OpeningChangeMm != results[j].OpeningChangeMm {
			return results[i].OpeningChangeMm < results[j].OpeningChangeMm
		}
		return results[i].Passport.ID < results[j].Passport.ID
	})
	writeJSON(w, http.StatusOK, results)
}

func evaluateMatch(p Passport, req MatchRequest) MatchResult {
	checks := []MatchCheck{}
	add := func(key, state, reason string) {
		checks = append(checks, MatchCheck{Key: key, State: state, Reason: reason})
	}
	compareDimension := func(key string, supply, demand float64) {
		if supply <= 0 || demand <= 0 {
			add(key, "unknown", "供應或需求尺寸缺漏")
			return
		}
		if absF(supply-demand) <= matchingDimensionToleranceMm {
			add(key, "pass", fmt.Sprintf("%.0f / %.0f mm；固定容差 ±%.0f mm", supply, demand, matchingDimensionToleranceMm))
		} else {
			add(key, "fail", fmt.Sprintf("%.0f / %.0f mm；超出固定 ±%.0f mm", supply, demand, matchingDimensionToleranceMm))
		}
	}
	compareText := func(key, supply, demand string, optional bool) {
		if strings.TrimSpace(demand) == "" && optional {
			return
		}
		if strings.TrimSpace(supply) == "" || strings.TrimSpace(demand) == "" {
			add(key, "unknown", "供應或需求資料尚未確認")
		} else if strings.EqualFold(strings.TrimSpace(supply), strings.TrimSpace(demand)) {
			add(key, "pass", supply)
		} else {
			add(key, "fail", fmt.Sprintf("%s / %s", supply, demand))
		}
	}

	// Candidate eligibility (Approved / Available / quantity > 0) is handled by
	// the SQL query. Only the eight coloured B-model fields below affect the result.
	compareDimension("門寬", p.WidthMm, req.WidthMm)
	compareDimension("門高", p.HeightMm, req.HeightMm)
	if req.LeafThicknessMm <= 0 {
		add("門扇厚度", "unknown", "B 模型需求尚未填寫必填的門扇厚度")
	} else if p.LeafThicknessMm <= 0 {
		add("門扇厚度", "unknown", "A 模型沒有明確門扇厚度／實測值")
	} else if absF(p.LeafThicknessMm-req.LeafThicknessMm) <= 1 {
		add("門扇厚度", "pass", fmt.Sprintf("%.0f / %.0f mm", p.LeafThicknessMm, req.LeafThicknessMm))
	} else {
		add("門扇厚度", "fail", fmt.Sprintf("%.0f / %.0f mm；第一版要求一致", p.LeafThicknessMm, req.LeafThicknessMm))
	}
	compareText("開啟方式", p.OperationType, req.OperationType, false)
	compareText("開門方向", p.SwingHanding, req.SwingHanding, false)
	compareText("室內外用途", canonicalMatchUse(p.UseType), canonicalMatchUse(req.UseType), false)
	compareText("材質", p.Material, req.MaterialPreference, false)
	if req.ClearWidthMinMm <= 0 {
		add("有效通行淨寬", "unknown", "B 模型需求尚未填寫必填的有效通行淨寬")
	} else if p.ClearWidthMm <= 0 {
		add("有效通行淨寬", "unknown", "A 護照缺少明確淨寬；不能以門寬代替")
	} else if p.ClearWidthMm >= req.ClearWidthMinMm {
		add("有效通行淨寬", "pass", fmt.Sprintf("A %.0f ≥ B 最低 %.0f mm", p.ClearWidthMm, req.ClearWidthMinMm))
	} else {
		add("有效通行淨寬", "fail", fmt.Sprintf("%.0f < %.0f mm", p.ClearWidthMm, req.ClearWidthMinMm))
	}
	result := "Matched"
	for _, check := range checks {
		if check.State == "fail" {
			result = "Not Matched"
			break
		}
		if check.State == "unknown" {
			result = "Pending Review"
		}
	}
	reasons := []string{}
	for _, check := range checks {
		if check.State != "pass" {
			reasons = append(reasons, check.Key+"："+check.Reason)
		}
	}
	// Ranking also uses only coloured dimensions; reference/audit fields never
	// change pass/fail or ordering.
	openingChange := absF(p.WidthMm-req.WidthMm) + absF(p.HeightMm-req.HeightMm)
	return MatchResult{Passport: p, Result: result, Checks: checks, Excluded: result == "Not Matched", ExcludeReasons: reasons, OpeningChangeMm: openingChange}
}

// Normalize only known aliases at comparison time; preserve stored passport
// values and leave missing or unrecognized uses for the existing checks.
func canonicalMatchUse(value string) string {
	switch strings.ToLower(strings.TrimSpace(value)) {
	case "外部出入口", "外部", "室外", "exterior":
		return "Exterior"
	default:
		return strings.TrimSpace(value)
	}
}

func absF(value float64) float64 {
	if value < 0 {
		return -value
	}
	return value
}

// PATCH /passports/{id}/review — A-model review and supply qualification.
func (s *Server) handleReviewPassport(w http.ResponseWriter, r *http.Request) {
	id, err := strconv.ParseInt(r.PathValue("id"), 10, 64)
	if err != nil {
		writeErr(w, http.StatusBadRequest, "id 須為數字")
		return
	}
	old, err := getPassportByID(s.DB, id)
	if err == sql.ErrNoRows {
		writeErr(w, http.StatusNotFound, "找不到護照")
		return
	}
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	if old.Status != "Available" && old.Status != "Unavailable" {
		writeErr(w, http.StatusConflict, "護照已預留或配置，不能直接變更審核")
		return
	}
	var review ReviewPassportRequest
	if err := json.NewDecoder(r.Body).Decode(&review); err != nil {
		writeErr(w, http.StatusBadRequest, "JSON 解析失敗: "+err.Error())
		return
	}
	if review.Revision != old.Revision {
		writeErr(w, http.StatusConflict, "護照資料已更新，請重新載入")
		return
	}
	if review.Quantity < 0 {
		writeErr(w, http.StatusBadRequest, "供應數量不能小於 0")
		return
	}
	reviewed := old
	reviewed.WidthMm = review.WidthMm
	reviewed.HeightMm = review.HeightMm
	reviewed.DimensionDefinition = review.DimensionDefinition
	reviewed.LeafThicknessMm = review.LeafThicknessMm
	reviewed.OperationType = review.OperationType
	reviewed.SwingHanding = review.SwingHanding
	reviewed.UseType = review.UseType
	reviewed.Material = review.Material
	reviewed.ClearWidthMm = review.ClearWidthMm
	reviewed.ReuseStatus = review.ReuseStatus
	if err := validateReview(review.ReuseStatus, review.ReviewNote, review.InspectedBy, reviewed.DimensionDefinition, reviewed.WidthMm, reviewed.HeightMm); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	if err := validateRequiredCore(reviewed); err != nil {
		writeErr(w, http.StatusBadRequest, err.Error())
		return
	}
	markReviewSources(&reviewed, old)
	sourcesJSON := mustJSON(reviewed.FieldSources, "{}")
	status := supplyStatus(review.ReuseStatus, review.Quantity)
	now := time.Now().UTC().Format(time.RFC3339)
	reviewResult, err := s.DB.Exec(`UPDATE passports SET width_mm=?, height_mm=?, dimension_definition=?, leaf_thickness_mm=?, operation_type=?, swing_handing=?, use_type=?, material=?, clear_width_mm=?, fire_rating=?, fire_minutes=?,
		reuse_status=?, condition_text=?, condition_grade=?, review_note=?, inspected_by=?, inspected_on=?, quantity=?, price_ntd=?, fire_certificate_reference=?, fire_certificate_verified=?, status=?, field_sources_json=?, revision=revision+1, updated_at=? WHERE id=? AND revision=? AND status IN ('Available','Unavailable')`,
		reviewed.WidthMm, reviewed.HeightMm, reviewed.DimensionDefinition, reviewed.LeafThicknessMm, reviewed.OperationType, reviewed.SwingHanding, reviewed.UseType, reviewed.Material, reviewed.ClearWidthMm, reviewed.FireRating, reviewed.FireMinutes,
		review.ReuseStatus, review.Condition, review.ConditionGrade, review.ReviewNote, review.InspectedBy, review.InspectedOn, review.Quantity, review.PriceNtd, reviewed.FireCertificateReference, boolInt(reviewed.FireCertificateVerified), status, sourcesJSON, now, id, old.Revision)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	changed, err := reviewResult.RowsAffected()
	if err != nil || changed != 1 {
		writeErr(w, http.StatusConflict, "護照已被其他操作更新，請重新載入")
		return
	}
	logPassportEvent(s.DB, id, "review", review.InspectedBy, review)
	out, _ := getPassportByID(s.DB, id)
	writeJSON(w, http.StatusOK, out)
}

func markReviewSources(reviewed *Passport, old Passport) {
	if reviewed.FieldSources == nil {
		reviewed.FieldSources = map[string]FieldSource{}
	}
	mark := func(key, definition string) {
		reviewed.FieldSources[key] = FieldSource{Kind: "manual", Definition: definition, Verification: "由 Revit 外掛使用者人工填寫／確認"}
	}
	if absF(reviewed.WidthMm-old.WidthMm) > 0.01 {
		mark("width", "人工確認門寬")
	}
	if absF(reviewed.HeightMm-old.HeightMm) > 0.01 {
		mark("height", "人工確認門高")
	}
	if reviewed.DimensionDefinition != old.DimensionDefinition {
		mark("dimensionDefinition", "nominal／leaf／frame_outer")
	}
	if absF(reviewed.LeafThicknessMm-old.LeafThicknessMm) > 0.01 {
		mark("leafThickness", "人工確認門扇厚度")
	}
	if reviewed.OperationType != old.OperationType {
		mark("operationType", "人工確認")
	}
	if reviewed.SwingHanding != old.SwingHanding {
		mark("swingHanding", "人工確認")
	}
	if reviewed.UseType != old.UseType {
		mark("useType", "人工確認")
	}
	if reviewed.Material != old.Material {
		mark("material", "人工確認")
	}
	if absF(reviewed.ClearWidthMm-old.ClearWidthMm) > 0.01 {
		mark("clearWidth", "有效通行淨寬，非名義門寬")
	}
}

// PATCH /passports/{id} — reservation and B-1 allocation lifecycle.
func (s *Server) handlePatchPassport(w http.ResponseWriter, r *http.Request) {
	id, err := strconv.ParseInt(r.PathValue("id"), 10, 64)
	if err != nil {
		writeErr(w, http.StatusBadRequest, "id 須為數字")
		return
	}
	old, err := getPassportByID(s.DB, id)
	if err == sql.ErrNoRows {
		writeErr(w, http.StatusNotFound, "找不到護照")
		return
	}
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	var body WorkflowRequest
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		writeErr(w, http.StatusBadRequest, "JSON 解析失敗: "+err.Error())
		return
	}
	if body.ExpectedRevision == nil || *body.ExpectedRevision < 1 || strings.TrimSpace(body.Actor) == "" ||
		!validPassportStatus[body.Status] || strings.TrimSpace(body.TargetModel) == "" ||
		strings.TrimSpace(body.TargetElementID) == "" || strings.TrimSpace(body.TargetUniqueID) == "" {
		writeErr(w, http.StatusBadRequest, "請更新外掛；預留／配置須提供版本、操作者及完整目標門識別")
		return
	}
	updated, err := planWorkflow(old, body)
	if err != nil {
		writeErr(w, http.StatusConflict, err.Error())
		return
	}
	now := time.Now().UTC().Format(time.RFC3339)
	tx, err := s.DB.Begin()
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	defer tx.Rollback()
	result, err := tx.Exec(`UPDATE passports SET status=?, target_model=?, target_element_id=?, target_unique_id=?, reserved_by=?, revision=revision+1, updated_at=? WHERE id=? AND revision=? AND status=? AND (? <> 'Available' OR (reuse_status='Approved' AND quantity>0))`,
		updated.Status, updated.TargetModel, updated.TargetElementID, updated.TargetUniqueID, updated.ReservedBy, now, id, *body.ExpectedRevision, old.Status, old.Status)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	changed, err := result.RowsAffected()
	if err != nil || changed != 1 {
		writeErr(w, http.StatusConflict, "護照已被其他操作更新，請重新載入；未改動配置")
		return
	}
	if _, err := tx.Exec(`INSERT INTO passport_events(passport_id,action,actor,details_json) VALUES(?,?,?,?)`, id, "workflow:"+updated.Status, body.Actor, mustJSON(body, "{}")); err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	if err := tx.Commit(); err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	out, err := getPassportByID(s.DB, id)
	if err != nil {
		writeErr(w, 500, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, out)
}

const passportColumns = `id, passport_id, category, source_model, source_unique_id, source_element_id, family, type_name,
	width_mm, height_mm, dimension_definition, leaf_thickness_mm, frame_dimensions, operation_type, swing_handing, use_type,
	clear_width_mm, leaf_count, material, fire_rating, fire_minutes, fire_certificate_reference, fire_certificate_verified,
	condition_text, condition_grade, reuse_status, review_note, quantity, price_ntd,
	placement_requirements, inspected_by, inspected_on, status, target_model, target_element_id, target_unique_id, reserved_by, photo,
	revision, raw_parameters_json, field_sources_json, mapping_warnings_json, created_at, updated_at`

type rowScanner interface{ Scan(dest ...any) error }

func scanPassportRow(row rowScanner) (Passport, error) {
	var p Passport
	var rawJSON, sourcesJSON, warningsJSON string
	var fireVerified int
	err := row.Scan(&p.ID, &p.PassportID, &p.Category, &p.SourceModel, &p.SourceUniqueID, &p.SourceElementID, &p.Family, &p.TypeName,
		&p.WidthMm, &p.HeightMm, &p.DimensionDefinition, &p.LeafThicknessMm, &p.FrameDimensions, &p.OperationType, &p.SwingHanding, &p.UseType,
		&p.ClearWidthMm, &p.LeafCount, &p.Material, &p.FireRating, &p.FireMinutes, &p.FireCertificateReference, &fireVerified,
		&p.Condition, &p.ConditionGrade, &p.ReuseStatus, &p.ReviewNote, &p.Quantity, &p.PriceNtd,
		&p.PlacementRequirements, &p.InspectedBy, &p.InspectedOn, &p.Status, &p.TargetModel, &p.TargetElementID, &p.TargetUniqueID, &p.ReservedBy, &p.Photo,
		&p.Revision, &rawJSON, &sourcesJSON, &warningsJSON, &p.CreatedAt, &p.UpdatedAt)
	if err != nil {
		return p, err
	}
	p.FireCertificateVerified = fireVerified != 0
	_ = json.Unmarshal([]byte(rawJSON), &p.RawParameters)
	_ = json.Unmarshal([]byte(sourcesJSON), &p.FieldSources)
	_ = json.Unmarshal([]byte(warningsJSON), &p.MappingWarnings)
	applyPassportDefaults(&p)
	recoverLeafThickness(&p)
	omitRetiredFields(&p)
	return p, nil
}

func scanPassport(rows *sql.Rows) (Passport, error) { return scanPassportRow(rows) }

func getPassportByID(db *sql.DB, id int64) (Passport, error) {
	return scanPassportRow(db.QueryRow(`SELECT `+passportColumns+` FROM passports WHERE id=?`, id))
}

func mustJSON(value any, fallback string) string {
	encoded, err := json.Marshal(value)
	if err != nil {
		return fallback
	}
	return string(encoded)
}

func boolInt(value bool) int {
	if value {
		return 1
	}
	return 0
}

func logPassportEvent(db *sql.DB, passportID int64, action, actor string, details any) {
	_, _ = db.Exec(`INSERT INTO passport_events(passport_id,action,actor,details_json) VALUES(?,?,?,?)`, passportID, action, actor, mustJSON(details, "{}"))
}
