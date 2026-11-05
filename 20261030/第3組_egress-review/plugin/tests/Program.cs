using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Group3.EgressExporter;

static class Program
{
    static void Check(bool condition, string description) { if (!condition) throw new Exception(description); Console.WriteLine("PASS " + description); }
    static double[] P(double x, double y) => new[] { x, y };
    static void Reject(Action action, string description)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, description);
    }
    static void Main()
    {
        var ring = Geometry.Ring(new[] { P(0,0), P(10,0), P(10,4), P(0,4), P(0,0) });
        Check(ring.Count == 4 && Geometry.Area(ring) == 40, "closed boundary removes duplicated endpoint without changing area");
        var path = Geometry.Chain(new List<List<double[]>> { new List<double[]> { P(0,0), P(1,0) }, new List<double[]> { P(2,0), P(1,0) } });
        Check(path.Count == 3 && path[2][0] == 2, "reversed corridor segments join in order");
        bool rejected = false;
        try { Geometry.Chain(new List<List<double[]>> { new List<double[]> { P(0,0), P(1,0) }, new List<double[]> { P(2,0), P(3,0) } }); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "disconnected corridor is rejected instead of bridged");
        var lShape = new List<double[]> { P(0,0), P(3,0), P(3,1), P(1,1), P(1,3), P(0,3) };
        Check(!Geometry.ContainsPath(lShape, new List<double[]> { P(0.5,2.5), P(2.5,0.5) }), "concave room rejects shortcut outside its boundary");
        Check(Geometry.ContainsPath(lShape, new List<double[]> { P(0.5,2.5), P(0.5,0.5), P(2.5,0.5) }), "corridor bend inside room is retained");
        var file = Path.Combine(Path.GetTempPath(), "group3-test-" + Guid.NewGuid() + ".json");
        try
        {
            var snapshot = new Snapshot { project = new ProjectData { model_name = "房間 <A> & 中文" } };
            snapshot.rooms.Add(new RoomData { element_id = "9007199254740993", boundary = ring });
            JsonFile.Write(file, snapshot);
            var bytes = File.ReadAllBytes(file);
            Check(!(bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191), "JSON has no UTF-8 BOM");
            var copy = JsonFile.Read<Snapshot>(file);
            Check(copy.rooms[0].element_id == "9007199254740993", "64-bit element ID remains a string");
            Check(copy.rooms[0].area_m2 == null && copy.rooms[0].is_habitable == null, "unknown fields survive as null");
            Check(copy.project.model_name == snapshot.project.model_name, "Chinese and special characters round-trip");
            JsonFile.Write(file, snapshot);
            Check(bytes.SequenceEqual(File.ReadAllBytes(file)), "same snapshot serializes deterministically and replaces atomically");
            var vertices = new[] { new[] { 10.0, 20.0, 8.0 }, new[] { 11.0, 20.2, 10.0 } };
            var fingerprint = PointRules.Fingerprint(vertices, "door-uid|type");
            var selected = new PickedPointSetting { point_m = new[] { 10.5, 20.1, 8.0 },
                geometry_fingerprint = fingerprint, source = "user_picked_plan_point" };
            PointRules.ValidatePoint(selected, fingerprint, 8, vertices[0], vertices[1], 0.5);
            Check(fingerprint == PointRules.Fingerprint(vertices.Reverse(), "door-uid|type"), "geometry fingerprint is stable across enumeration order");
            var moved = vertices.Select(v => new[] { v[0] + 1, v[1], v[2] }).ToArray();
            Reject(() => PointRules.ValidatePoint(selected, PointRules.Fingerprint(moved, "door-uid|type"), 8,
                moved[0], moved[1], 0.5), "moving the door invalidates an old point before it can enter the network");
            Reject(() => PointRules.ValidatePoint(selected, fingerprint, 12, vertices[0], vertices[1], 0.5), "point from another floor is rejected");
            Reject(() => PointRules.ValidatePoint(new PickedPointSetting { point_m = new[] { 10.5, 20.1, double.NaN },
                geometry_fingerprint = fingerprint }, fingerprint, 8, vertices[0], vertices[1], 0.5), "non-finite point is rejected");
            Reject(() => PointRules.ValidatePoint(new PickedPointSetting { point_m = new[] { 50.0, 50.0, 8.0 },
                geometry_fingerprint = fingerprint }, fingerprint, 8, vertices[0], vertices[1], 0.5), "distant click is rejected");
            Reject(() => PointRules.ValidatePoint(new PickedPointSetting { point_m = selected.point_m }, fingerprint,
                8, vertices[0], vertices[1], 0.5), "legacy point without a geometry fingerprint requires confirmation");
            File.WriteAllText(file, "{\"model_guid\":\"model\",\"level_id\":\"L3\",\"phase_id\":\"new\",\"rooms\":[],\"stairs\":[{\"unique_id\":\"stair\",\"is_direct_stair\":null,\"endpoint_type\":\"first_step\",\"endpoint_unique_id\":\"curve\",\"curve_end\":1}],\"corridors\":[]}");
            var legacy = PointRules.Normalize(JsonFile.Read<ExportSettings>(file));
            Check(legacy.doors.Count == 0 && legacy.stairs[0].picked_point == null && legacy.stairs[0].curve_end == 1,
                "old JSON without door or picked-point fields remains readable");
            PointRules.ValidateScope(legacy, "model", "L3", "new");
            Reject(() => PointRules.ValidateScope(legacy, "model", "L1", "new"), "settings for a different floor cannot be applied");
            Reject(() => PointRules.ValidateScope(legacy, "other", "L3", "new"), "settings for another model cannot be applied");
            Reject(() => PointRules.ValidateScope(legacy, "model", "L3", "existing"), "settings for another phase cannot be applied");
            PointRules.SetDoor(legacy, new DoorSetting { unique_id = "door", picked_point = selected });
            PointRules.SetDoor(legacy, new DoorSetting { unique_id = "door", picked_point = selected });
            Check(legacy.doors.Count == 1 && legacy.stairs[0].endpoint_unique_id == "curve", "updating a door keeps unrelated staircase settings and does not duplicate the door");
            PointRules.SetStair(legacy, new StairSetting { unique_id = "stair", endpoint_type = "first_step", picked_point = selected });
            JsonFile.Write(file, legacy);
            var roundTrip = JsonFile.Read<ExportSettings>(file);
            Check(roundTrip.stairs.Count == 1 && roundTrip.stairs[0].picked_point.point_m.SequenceEqual(selected.point_m) &&
                roundTrip.stairs[0].is_direct_stair == null && roundTrip.doors.Count == 1,
                "picked staircase endpoint and unknown qualification persist without losing door settings");
            snapshot.unresolved_doors.Add(new DoorData { element_id = "door", position_candidate = new[] { 10.5, 20.1 },
                position_source = "bounding_box_candidate", position_confirmed = false });
            JsonFile.Write(file, snapshot);
            copy = JsonFile.Read<Snapshot>(file);
            Check(copy.doors.Count == 0 && copy.unresolved_doors.Count == 1 && copy.unresolved_doors[0].position == null,
                "candidate doors remain outside routing doors and preserve their suggested coordinate");
            var page = ReportBuilder.Inject("a /*G3_IMPORT*/null b /*G3_SETTINGS*/null", "\uFEFF{\"name\":\"</script>\"}", "{}");
            Check(page == "a {\"name\":\"\\u003c/script>\"} b {}", "the snapshot and settings go into the page without a BOM and cannot end the script early");
            Reject(() => ReportBuilder.Inject("no marker", "{}", "{}"), "a page template without the marker is rejected");
            Reject(() => ReportBuilder.Inject("/*G3_IMPORT*/null /*G3_IMPORT*/null /*G3_SETTINGS*/null", "{}", "{}"), "a page template with two markers is rejected");
            Check(ReportBuilder.SettingsJson("H-2", 4, true).Contains("\"use_group\":\"H-2\"") && ReportBuilder.SettingsJson("H-2", 4, true).Contains("\"floor_number\":4,\"below_ground\":false,\"fire_resistant\":true"), "review settings are written for the page");
            Check(ReportBuilder.SettingsJson("H-2", -1, null).Contains("\"below_ground\":true,\"fire_resistant\":null"), "a negative floor is below ground and an unconfirmed fire rating stays null");
            Reject(() => ReportBuilder.SettingsJson("Z-9", 4, true), "an unknown use group is rejected");
            var column = new List<double[]> { P(4,1), P(5,1), P(5,2), P(4,2) };
            var outerRing = Geometry.OuterLoop(new List<List<double[]>> { column, ring }, out var innerLoops);
            Check(ReferenceEquals(outerRing, ring) && innerLoops.Count == 1 && ReferenceEquals(innerLoops[0], column),
                "the largest loop is the room outline and a column inside it becomes an inner loop");
            Check(Geometry.OuterLoop(new List<List<double[]>> { ring }, out innerLoops) == ring && innerLoops.Count == 0, "a room with one loop has no inner loops");
            var detached = new List<double[]> { P(20,0), P(21,0), P(21,1), P(20,1) };
            Check(Geometry.OuterLoop(new List<List<double[]>> { ring, detached }, out innerLoops) == null, "a detached region is not treated as an inner loop");
            var grouped = WarningSummary.Group(new[] {
                new WarningData { code = "ROOM_NOT_ENCLOSED", element_id = "9", severity = "error", message = "m" },
                new WarningData { code = "ROOM_NOT_ENCLOSED", element_id = "8", severity = "error", message = "m" },
                new WarningData { code = "OBSTACLE_SCOPE", message = "m" } });
            Check(grouped.Count == 2 && grouped[0].code == "ROOM_NOT_ENCLOSED" && grouped[0].count == 2 && grouped[0].element_ids.Count == 2,
                "same-code warnings collapse to one row with errors listed first");
            Check(grouped.Single(g => g.code == "OBSTACLE_SCOPE").count == 1, "a project-wide warning counts once");
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
