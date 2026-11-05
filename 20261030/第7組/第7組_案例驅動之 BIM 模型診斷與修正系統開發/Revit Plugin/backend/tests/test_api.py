import os
import tempfile

import ifcopenshell

os.environ["CASEBASE_DATA_DIR"] = tempfile.mkdtemp()

from fastapi.testclient import TestClient

from app.main import app

client = TestClient(app)


def payload(category="Doors", type_name="900 x 2100mm"):
    return {
        "title": "Test issue",
        "description": "Door has a clearance conflict.",
        "category": "Coordination",
        "priority": "High",
        "elements": [{
            "unique_id": "test-unique-id",
            "element_id": "42",
            "category": category,
            "family": "Single-Flush",
            "type_name": type_name,
            "level": "Level 1",
            "dimensions": {"Width": 900.0, "Height": 2100.0},
            "parameters": {"Fire Rating": "60 min"},
            "ifc_class": "IfcDoor",
        }],
    }


def test_search_uses_metadata_and_returns_explainable_match():
    with client:
        response = client.post("/cases/search", json={"element": payload()["elements"][0]})
    assert response.status_code == 200
    assert response.json()[0]["score"] >= 0.7
    assert response.json()[0]["evidence"]


def test_zero_match_is_safe():
    with client:
        response = client.post("/cases/search", json={"element": payload("Furniture", "Desk")["elements"][0]})
    assert response.status_code == 200
    assert response.json() == []


def test_issue_can_be_created_without_ifc_and_resolved():
    with client:
        create = client.post("/issues", data={"payload": __import__("json").dumps(payload())})
        assert create.status_code == 201
        issue_id = create.json()["id"]
        assert create.json()["matches"]
        resolved = client.post(f"/issues/{issue_id}/resolve", json={
            "confirmed_cause": "Incorrect door handing.",
            "verified_fix": "Reverse door handing.",
            "resolution_notes": "Verified in coordination view.",
        })
    assert resolved.status_code == 200
    assert resolved.json()["status"] == "Resolved"


def test_rejects_non_ifc_upload():
    with client:
        response = client.post(
            "/issues",
            data={"payload": __import__("json").dumps(payload())},
            files={"ifc_file": ("not-ifc.txt", b"not an IFC", "text/plain")},
        )
    assert response.status_code == 400
    assert "IFC" in response.json()["detail"]


def test_accepts_ifc_with_only_requested_product_scope(tmp_path):
    global_id = "0YvctVUKr0A8l$AqO4wX0V"
    model = ifcopenshell.file(schema="IFC4")
    model.create_entity("IfcWall", GlobalId=global_id, Name="Selected wall")
    ifc_path = tmp_path / "selection.ifc"
    model.write(str(ifc_path))
    data = payload()
    data["ifc_global_ids"] = [global_id]
    with client:
        response = client.post(
            "/issues",
            data={"payload": __import__("json").dumps(data)},
            files={"ifc_file": ("selection.ifc", ifc_path.read_bytes(), "application/x-step")},
        )
    assert response.status_code == 201
    assert response.json()["ifc_file_name"] == "selection.ifc"
