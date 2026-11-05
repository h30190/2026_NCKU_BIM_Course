package main

// RawParameter preserves the complete Revit instance/type parameter snapshot.
// Values are evidence from the model, not measurements or certifications.
type RawParameter struct {
	Scope          string   `json:"scope"`
	Name           string   `json:"name"`
	ParameterID    string   `json:"parameterId"`
	GUID           string   `json:"guid,omitempty"`
	Builtin        string   `json:"builtin,omitempty"`
	Storage        string   `json:"storage"`
	DisplayValue   string   `json:"displayValue"`
	RawValue       string   `json:"rawValue"`
	Unit           string   `json:"unit,omitempty"`
	NumericValueMm *float64 `json:"numericValueMm,omitempty"`
}

// FieldSource records how one canonical passport field was obtained.
type FieldSource struct {
	Kind          string `json:"kind"` // bim / cobie / manual / unknown
	Scope         string `json:"scope,omitempty"`
	ParameterName string `json:"parameterName,omitempty"`
	ParameterID   string `json:"parameterId,omitempty"`
	Unit          string `json:"unit,omitempty"`
	Definition    string `json:"definition,omitempty"`
	Verification  string `json:"verification,omitempty"`
}

// Passport is the single source of truth shared by Revit and the read-only dashboard.
type Passport struct {
	ID              int64  `json:"id"`
	PassportID      string `json:"passportId"`
	Category        string `json:"category"`
	SourceModel     string `json:"sourceModel"`
	SourceUniqueID  string `json:"sourceUniqueId"`
	SourceElementID string `json:"sourceElementId"`
	Family          string `json:"family"`
	TypeName        string `json:"typeName"`

	WidthMm             float64 `json:"widthMm"`
	HeightMm            float64 `json:"heightMm"`
	DimensionDefinition string  `json:"dimensionDefinition"`
	LeafThicknessMm     float64 `json:"leafThicknessMm"`
	FrameDimensions     string  `json:"frameDimensions"`
	OperationType       string  `json:"operationType"`
	SwingHanding        string  `json:"swingHanding"`
	UseType             string  `json:"useType"`
	ClearWidthMm        float64 `json:"clearWidthMm"`
	LeafCount           int     `json:"leafCount"`
	Material            string  `json:"material"`

	FireRating               string  `json:"-"`
	FireMinutes              int     `json:"-"`
	FireCertificateReference string  `json:"-"`
	FireCertificateVerified  bool    `json:"-"` // Legacy metadata; no longer a review or match criterion.
	Condition                string  `json:"condition"`
	ConditionGrade           string  `json:"conditionGrade"`
	ReuseStatus              string  `json:"reuseStatus"`
	ReviewNote               string  `json:"reviewNote"`
	Quantity                 int     `json:"quantity"`
	PriceNtd                 float64 `json:"priceNtd"`

	PlacementRequirements string `json:"placementRequirements"`
	InspectedBy           string `json:"inspectedBy"`
	InspectedOn           string `json:"inspectedOn"`
	Photo                 string `json:"photo"`

	Status          string `json:"status"` // Unavailable / Available / Reserved / Allocated / Installed
	TargetModel     string `json:"targetModel"`
	TargetElementID string `json:"targetElementId"`
	Revision        int    `json:"revision"`
	CreatedAt       string `json:"createdAt"`
	UpdatedAt       string `json:"updatedAt"`

	RawParameters   []RawParameter         `json:"rawParameters"`
	FieldSources    map[string]FieldSource `json:"fieldSources"`
	MappingWarnings []string               `json:"mappingWarnings"`
}

var validPassportStatus = map[string]bool{
	"Unavailable": true,
	"Available":   true,
	"Reserved":    true,
	"Allocated":   true,
	"Installed":   true,
}

var validReuseStatus = map[string]bool{
	"Pending":         true,
	"Approved":        true,
	"Repair Required": true,
	"Rejected":        true,
}

// MatchRequest is confirmed by the designer for one selected B-model door.
type MatchRequest struct {
	WidthMm              float64 `json:"widthMm"`
	HeightMm             float64 `json:"heightMm"`
	DimensionDefinition  string  `json:"dimensionDefinition"`
	ToleranceMm          float64 `json:"toleranceMm"`
	LeafThicknessMm      float64 `json:"leafThicknessMm"`
	OperationType        string  `json:"operationType"`
	SwingHanding         string  `json:"swingHanding"`
	UseType              string  `json:"useType"`
	MaterialPreference   string  `json:"materialPreference"`
	ClearWidthMinMm      float64 `json:"clearWidthMinMm"`
	LeafCount            int     `json:"leafCount"`
	DoorOpeningWidthMm   float64 `json:"doorOpeningWidthMm"`
	HostWallThicknessMm  float64 `json:"hostWallThicknessMm"`
	FireRequirementKnown bool    `json:"-"`
	FireRequired         bool    `json:"-"`
	FireMinutes          int     `json:"-"`
	InstallationReviewed bool    `json:"installationReviewed"`
	Basis                string  `json:"basis"`
	TargetModel          string  `json:"targetModel"`
	TargetUniqueID       string  `json:"targetUniqueId"`
}

type MatchCheck struct {
	Key    string `json:"key"`
	State  string `json:"state"` // pass / unknown / fail
	Reason string `json:"reason"`
}

type MatchResult struct {
	Passport        Passport     `json:"passport"`
	Result          string       `json:"result"` // Matched / Pending Review / Not Matched
	Checks          []MatchCheck `json:"checks"`
	Excluded        bool         `json:"excluded"`
	ExcludeReasons  []string     `json:"excludeReasons,omitempty"`
	OpeningChangeMm float64      `json:"openingChangeMm"`
}

type ReviewPassportRequest struct {
	WidthMm                  float64 `json:"widthMm"`
	HeightMm                 float64 `json:"heightMm"`
	DimensionDefinition      string  `json:"dimensionDefinition"`
	LeafThicknessMm          float64 `json:"leafThicknessMm"`
	OperationType            string  `json:"operationType"`
	SwingHanding             string  `json:"swingHanding"`
	UseType                  string  `json:"useType"`
	Material                 string  `json:"material"`
	ClearWidthMm             float64 `json:"clearWidthMm"`
	FireRating               string  `json:"-"`
	FireMinutes              int     `json:"-"`
	ReuseStatus              string  `json:"reuseStatus"`
	Condition                string  `json:"condition"`
	ConditionGrade           string  `json:"conditionGrade"`
	ReviewNote               string  `json:"reviewNote"`
	InspectedBy              string  `json:"inspectedBy"`
	InspectedOn              string  `json:"inspectedOn"`
	Quantity                 int     `json:"quantity"`
	PriceNtd                 float64 `json:"priceNtd"`
	FireCertificateReference string  `json:"-"`
	FireCertificateVerified  bool    `json:"-"` // Legacy compatibility only.
	Revision                 int     `json:"revision"`
}
