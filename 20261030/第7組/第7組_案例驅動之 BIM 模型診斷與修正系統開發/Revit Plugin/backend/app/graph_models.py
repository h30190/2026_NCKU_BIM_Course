"""Transport models shared by graph, persistence and retrieval services."""
from enum import Enum
from typing import Any, Literal
from pydantic import BaseModel, Field, model_validator, computed_field
from .case_scope import classify


class IssueType(str, Enum):
    GEOMETRY_CLASH = 'GEOMETRY_CLASH'
    SPATIAL_CLEARANCE = 'SPATIAL_CLEARANCE'
    PARAMETER_ERROR = 'PARAMETER_ERROR'
    IFC_MAPPING_ERROR = 'IFC_MAPPING_ERROR'
    CONNECTION_ERROR = 'CONNECTION_ERROR'
    DESIGN_CONFLICT = 'DESIGN_CONFLICT'
    CONSTRUCTION_ISSUE = 'CONSTRUCTION_ISSUE'
    OTHER = 'OTHER'


class Node(BaseModel):
    node_id: str = Field(min_length=1, max_length=500)
    node_type: Literal['ELEMENT', 'SPACE', 'LEVEL', 'SYSTEM', 'MATERIAL', 'ISSUE', 'SOLUTION', 'PROJECT'] = 'ELEMENT'
    name: str = Field(max_length=500)
    properties: dict[str, Any] = Field(default_factory=dict)
    source_document: str = Field(min_length=1)
    source_element_id: str | None = None


class Edge(BaseModel):
    source: str
    target: str
    relationship_type: Literal['HOSTED_BY', 'CONTAINED_IN', 'ADJACENT_TO', 'CONNECTED_TO', 'INTERSECTS', 'BELONGS_TO_SYSTEM', 'LOCATED_ON_LEVEL', 'HAS_MATERIAL', 'HAS_ISSUE', 'RESOLVED_BY', 'RELATED_TO', 'NEARBY']
    properties: dict[str, Any] = Field(default_factory=dict)
    evidence: str = Field(min_length=1, max_length=2000)
    validation_status: Literal['verified', 'candidate', 'unverified']

    @model_validator(mode='after')
    def precise_intersection(self):
        if self.relationship_type == 'INTERSECTS' and self.validation_status != 'verified':
            raise ValueError('INTERSECTS requires verified geometry; use NEARBY for candidates')
        return self


class Graph(BaseModel):
    root: str
    nodes: list[Node] = Field(min_length=1, max_length=250)
    edges: list[Edge] = Field(default_factory=list, max_length=1000)
    model_version: str | None = None
    warnings: list[str] = Field(default_factory=list)

    @model_validator(mode='after')
    def valid_references(self):
        ids = {n.node_id for n in self.nodes}
        if len(ids) != len(self.nodes) or self.root not in ids:
            raise ValueError('Unique node IDs and an existing root are required')
        root = next(n for n in self.nodes if n.node_id == self.root)
        if root.node_type != 'ELEMENT' or not root.source_element_id:
            raise ValueError('Root must be an element with a persistent UniqueId')
        if any(e.source not in ids or e.target not in ids for e in self.edges):
            raise ValueError('All edge endpoints must exist')
        return self


class CaseElementType(BaseModel):
    document: str
    type_id: str
    category_id: str
    category: str
    family: str
    type_name: str
    complete: bool


class CaseClassification(BaseModel):
    schema_version: Literal[1] = 1
    element_type: CaseElementType
    issue_type: IssueType


class OriginElement(BaseModel):
    unique_id: str = Field(min_length=1)
    element_id: str = ''
    name: str = Field(default='', max_length=500)
    category: str | None = None
    role: Literal['root', 'related']


class CheckOrigin(BaseModel):
    """Structured reference to the model-check result an issue was recorded from."""
    schema_version: Literal[1] = 1
    kind: Literal['MODEL_CHECK'] = 'MODEL_CHECK'
    check: Literal['duplicate', 'overlap', 'clash', 'mark', 'warning']
    status: Literal['SUSPECT', 'OVERLAP', 'WARNING', 'UNKNOWN']
    result_id: str = Field(min_length=1, max_length=50)
    message: str = Field(max_length=5000)
    advice: str = Field(default='', max_length=2000)
    document: str = Field(min_length=1)
    range_mode: Literal['view', 'selected', 'plans']
    view_name: str = Field(default='', max_length=2000)
    views: list[dict[str, str]] = Field(default_factory=list, max_length=500)
    checked_at: str = Field(min_length=1, max_length=50)
    settings: dict[str, Any] = Field(default_factory=dict)
    element_count: int = Field(ge=1)
    elements: list[OriginElement] = Field(min_length=1, max_length=50)


class ShaftCheckOrigin(CheckOrigin):
    """Shaft findings retain rule evidence and actual linked-model identities."""
    kind: Literal['SHAFT_CHECK'] = 'SHAFT_CHECK'
    check: Literal['fire', 'maint', 'mep']
    status: Literal['Violation', 'Warning', 'DataMissing']
    range_mode: Literal['model'] = 'model'
    rule_id: Literal['A1', 'A2', 'A4', 'A6', 'A7', 'A8', 'A9', 'P1', 'P2', 'P3', 'P4', 'P5']
    rule_name: str = Field(max_length=200)
    nature: str = Field(max_length=100)
    reference: str = Field(default='', max_length=2000)
    actual: float | None = None
    required: float | None = None
    unit: str | None = None
    shaft_key: str = Field(min_length=1)
    shaft_label: str
    source_refs: list[dict[str, Any]] = Field(default_factory=list)


class Draft(BaseModel):
    title: str = Field(min_length=1, max_length=200)
    description: str = Field(min_length=1, max_length=10000)
    issue_type: IssueType
    priority: Literal['Low', 'Medium', 'High', 'Critical'] = 'Medium'
    notes: str = Field(default='', max_length=5000)
    graph: Graph
    origin: CheckOrigin | ShaftCheckOrigin | None = None

    @model_validator(mode='after')
    def origin_matches_root(self):
        if self.origin:
            root = next(n for n in self.graph.nodes if n.node_id == self.graph.root)
            roots = [e for e in self.origin.elements if e.role == 'root']
            if (len(roots) != 1 or roots[0].unique_id != root.source_element_id
                    or self.origin.document != root.source_document):
                raise ValueError('The check origin must reference the case root element')
        return self

    @computed_field
    @property
    def classification(self) -> CaseClassification:
        # Derive from evidence, including older revisions; client-supplied labels cannot override it.
        return CaseClassification.model_validate(classify(dict(
            issue_type=self.issue_type.value, graph=self.graph.model_dump())))


class ChangeRow(BaseModel):
    field: str = Field(max_length=200)
    before: str = Field(max_length=2000)
    after: str = Field(max_length=2000)
    # Replayable step (set_parameter, change_type, move, flip, rotate); None if informational.
    action: dict[str, Any] | None = None


class ElementChange(BaseModel):
    node_id: str
    name: str = Field(default='', max_length=500)
    element_id: str = ''
    unique_id: str = Field(min_length=1)
    ifc_global_id: str | None = None
    root: bool = False
    category: str | None = None
    family: str | None = None
    type_name: str | None = None
    type_id: str | None = None
    relations: list[str] = Field(default_factory=list)  # e.g. HOSTED_BY from the case root
    state: Literal['CHANGED', 'UNCHANGED', 'DELETED', 'UNAVAILABLE']
    match: str = ''
    changes: list[ChangeRow] = Field(default_factory=list, max_length=60)


class ModelChanges(BaseModel):
    """Recorded elements compared with the live model when a review step is taken."""
    schema_version: Literal[1] = 1
    case_id: int
    revision: int = Field(ge=1)
    document: str = Field(min_length=1)
    checked_at: str = Field(min_length=1, max_length=50)
    elements: list[ElementChange] = Field(max_length=40)


class Transition(BaseModel):
    status: Literal['REPORTED', 'UNDER_REVIEW', 'RESOLVED', 'VERIFIED', 'ARCHIVED']
    solution: str = Field(default='', max_length=10000)
    reviewer: str = Field(default='', max_length=200)
    changes: ModelChanges | None = None


class Retrieval(BaseModel):
    draft: Draft
    limit: int = Field(default=5, ge=1, le=20)
    candidate_limit: int = Field(default=1000, ge=1, le=10000)
    weights: dict[str, float] = Field(default_factory=lambda: dict(element=.30, relationship=.30, spatial=.25, issue=.15))

    @model_validator(mode='after')
    def valid_weights(self):
        import math
        if set(self.weights) != {'element', 'relationship', 'spatial', 'issue'} or any(not math.isfinite(v) or v < 0 for v in self.weights.values()) or not sum(self.weights.values()):
            raise ValueError('Provide four finite nonnegative component weights with positive total')
        return self


class ElementSearch(BaseModel):
    target: Node
    candidates: list[Node] = Field(default_factory=list, max_length=500)
    include_recorded: bool = False
    scope: Literal['model', 'view', 'recorded'] = 'model'
    limit: int = Field(default=20, ge=1, le=100)
    minimum_score: float = Field(default=0, ge=0, le=1)
    candidate_limit: int = Field(default=1000, ge=1, le=10000)
    truncated: bool = False

    @model_validator(mode='after')
    def persistent_elements(self):
        if any(n.node_type != 'ELEMENT' or not n.source_element_id for n in [self.target] + self.candidates):
            raise ValueError('Element search needs persistent element references')
        return self


class RelatedCaseSearch(BaseModel):
    target: Node
    issue_type: IssueType | Literal['ALL']
    verified_only: bool = False
    exclude_case_id: int | None = None
    limit: int = Field(default=10, ge=1, le=50)
    candidate_limit: int = Field(default=1000, ge=1, le=10000)

    @model_validator(mode='after')
    def persistent_target(self):
        if self.target.node_type != 'ELEMENT' or not self.target.source_element_id:
            raise ValueError('Case search needs a persistent model element and an issue type')
        return self
