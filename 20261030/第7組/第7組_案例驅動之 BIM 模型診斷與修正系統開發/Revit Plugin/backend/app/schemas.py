from __future__ import annotations

from datetime import datetime
from typing import Any, Literal

from pydantic import BaseModel, Field


class ElementSnapshot(BaseModel):
    unique_id: str
    element_id: str
    category: str | None = None
    family: str | None = None
    type_name: str | None = None
    level: str | None = None
    location: dict[str, float] | None = None
    dimensions: dict[str, float] = Field(default_factory=dict)
    parameters: dict[str, str] = Field(default_factory=dict)
    ifc_class: str | None = None
    ifc_global_id: str | None = None


class CaseSearchRequest(BaseModel):
    element: ElementSnapshot
    limit: int = Field(default=5, ge=1, le=20)


class Difference(BaseModel):
    field: str
    current: str | None
    historical: str | None


class CaseMatch(BaseModel):
    case_id: int
    title: str
    score: float
    evidence: list[str]
    differences: list[Difference]
    confirmed_cause: str
    verified_fix: str


class RfiCreate(BaseModel):
    project_id: str = "default"
    project_name: str = "Current Project"
    model_version: str | None = None
    title: str = Field(min_length=1, max_length=200)
    description: str = Field(min_length=1, max_length=10000)
    category: str = "Coordination"
    priority: Literal["Low", "Medium", "High", "Critical"] = "Medium"
    elements: list[ElementSnapshot] = Field(min_length=1)
    ifc_global_ids: list[str] = Field(default_factory=list)


class RfiSummary(BaseModel):
    id: int
    title: str
    priority: str
    status: str
    created_at: datetime


class RfiDetail(RfiSummary):
    description: str
    category: str
    elements: list[ElementSnapshot]
    ifc_file_name: str | None
    matches: list[CaseMatch]
    suggestions: list[str]


class ResolutionRequest(BaseModel):
    confirmed_cause: str = Field(min_length=1, max_length=5000)
    verified_fix: str = Field(min_length=1, max_length=5000)
    resolution_notes: str = Field(default="", max_length=5000)


class ResolutionResult(BaseModel):
    issue_id: int
    case_id: int
    status: str


class UploadResult(BaseModel):
    file_name: str
    byte_count: int
    verified_global_ids: list[str]
    metadata: dict[str, Any] = Field(default_factory=dict)
