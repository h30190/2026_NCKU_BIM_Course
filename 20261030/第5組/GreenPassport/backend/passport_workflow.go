package main

import "fmt"

// Revision and actor are workflow guards, not authentication credentials.
type WorkflowRequest struct {
	Status           string `json:"status"`
	TargetModel      string `json:"targetModel"`
	TargetElementID  string `json:"targetElementId"`
	TargetUniqueID   string `json:"targetUniqueId"`
	OriginalUniqueID string `json:"originalUniqueId"`
	OriginalTypeID   string `json:"originalTypeId"`
	NewTypeID        string `json:"newTypeId"`
	Reason           string `json:"reason"`
	Actor            string `json:"actor"`
	ExpectedRevision *int   `json:"expectedRevision"`
}

func planWorkflow(old Passport, body WorkflowRequest) (Passport, error) {
	conflict := func() (Passport, error) {
		return old, fmt.Errorf("護照已預留／配置給其他操作者或目標，或版本已過期；請重新載入")
	}
	if body.ExpectedRevision == nil || *body.ExpectedRevision != old.Revision {
		return conflict()
	}
	sameTarget := old.TargetModel == body.TargetModel && old.TargetElementID == body.TargetElementID &&
		(old.TargetUniqueID == "" || old.TargetUniqueID == body.TargetUniqueID)
	allowed := false
	switch old.Status {
	case "Available":
		allowed = body.Status == "Reserved" && old.ReuseStatus == "Approved" && old.Quantity > 0
	case "Reserved":
		allowed = old.ReservedBy != "" && body.Actor == old.ReservedBy && sameTarget &&
			(body.Status == "Reserved" || body.Status == "Allocated" || body.Status == "Available")
	case "Allocated":
		allowed = sameTarget && (body.Status == "Allocated" || body.Status == "Installed")
	case "Installed":
		allowed = sameTarget && body.Status == "Installed"
	}
	if !allowed {
		return conflict()
	}
	updated := old
	updated.Status = body.Status
	updated.TargetModel, updated.TargetElementID, updated.TargetUniqueID = body.TargetModel, body.TargetElementID, body.TargetUniqueID
	if old.Status == "Available" {
		updated.ReservedBy = body.Actor
	}
	if body.Status == "Available" {
		updated.TargetModel, updated.TargetElementID, updated.TargetUniqueID, updated.ReservedBy = "", "", "", ""
	}
	return updated, nil
}
