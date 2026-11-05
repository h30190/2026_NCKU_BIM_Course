using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace Group3.EgressExporter
{
    internal static class RevitPositions
    {
        internal static double M(double n) => UnitUtils.ConvertFromInternalUnits(n, UnitTypeId.Meters);
        internal static double[] Metres(XYZ p) => new[] { M(p.X), M(p.Y), M(p.Z) };
        internal static XYZ Internal(double[] p) => new XYZ(
            UnitUtils.ConvertToInternalUnits(p[0], UnitTypeId.Meters),
            UnitUtils.ConvertToInternalUnits(p[1], UnitTypeId.Meters),
            UnitUtils.ConvertToInternalUnits(p[2], UnitTypeId.Meters));
        internal static string Id(Element e) => e.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        internal static bool Exists(Element e, Phase phase)
        {
            var status = e.GetPhaseStatus(phase.Id);
            return e.DesignOption == null && (status == ElementOnPhaseStatus.New ||
                status == ElementOnPhaseStatus.Existing || status == ElementOnPhaseStatus.None);
        }
        internal static List<XYZ> BoxCorners(Element e)
        {
            var b = e.get_BoundingBox(null);
            if (b == null) throw new ArgumentException("元件沒有可用幾何範圍。");
            var points = new List<XYZ>();
            foreach (var x in new[] { b.Min.X, b.Max.X }) foreach (var y in new[] { b.Min.Y, b.Max.Y })
                foreach (var z in new[] { b.Min.Z, b.Max.Z }) points.Add(b.Transform.OfPoint(new XYZ(x, y, z)));
            return points;
        }
        static void GeometryPoints(GeometryElement geometry, Transform transform, List<double[]> points)
        {
            if (geometry == null) return;
            foreach (GeometryObject obj in geometry)
            {
                if (obj is GeometryInstance instance)
                    GeometryPoints(instance.GetSymbolGeometry(), transform.Multiply(instance.Transform), points);
                else if (obj is Solid solid)
                    foreach (Edge edge in solid.Edges) foreach (XYZ p in edge.Tessellate()) points.Add(Metres(transform.OfPoint(p)));
                else if (obj is Mesh mesh)
                    foreach (XYZ p in mesh.Vertices) points.Add(Metres(transform.OfPoint(p)));
                else if (obj is Curve curve)
                    foreach (XYZ p in curve.Tessellate()) points.Add(Metres(transform.OfPoint(p)));
            }
        }
        internal static string Fingerprint(Element e)
        {
            var points = BoxCorners(e).Select(Metres).ToList();
            using (var options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false })
                GeometryPoints(e.get_Geometry(options), Transform.Identity, points);
            if (e is FamilyInstance family)
            {
                var t = family.GetTransform();
                foreach (var p in new[] { XYZ.Zero, XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ }) points.Add(Metres(t.OfPoint(p)));
            }
            return PointRules.Fingerprint(points, e.UniqueId + "|" + e.GetTypeId().Value + "|" + e.LevelId.Value +
                "|" + e.CreatedPhaseId.Value + "|" + e.DemolishedPhaseId.Value);
        }
        // A geometry-box midpoint is only a suggestion. It never becomes a routing door without confirmation.
        internal static XYZ DoorCandidate(FamilyInstance door, Level level, out string source)
        {
            if (door.Location is LocationPoint location)
            {
                if (!PointRules.IsPoint(Metres(location.Point))) throw new ArgumentException("門的插入點不是有效座標。");
                source = "family_location_point";
                return location.Point;
            }
            var points = BoxCorners(door);
            if (points.Any(p => !PointRules.IsPoint(Metres(p)))) throw new ArgumentException("門的幾何範圍不是有效座標。");
            source = "bounding_box_candidate";
            return new XYZ((points.Min(p => p.X) + points.Max(p => p.X)) / 2,
                (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2, level.ProjectElevation);
        }
        internal static void Validate(Element element, Level level, PickedPointSetting saved, bool stair)
        {
            var box = BoxCorners(element).Select(Metres).ToList();
            var min = Enumerable.Range(0, 3).Select(i => box.Min(p => p[i])).ToArray();
            var max = Enumerable.Range(0, 3).Select(i => box.Max(p => p[i])).ToArray();
            // Selection-error tolerances, not legal egress thresholds.
            PointRules.ValidatePoint(saved, Fingerprint(element), M(level.ProjectElevation), min, max, stair ? 3.0 : 0.5);
        }
    }
    internal static class LocalSettings
    {
        internal static string PathFor(Document doc, Level level, Phase phase)
        {
            var model = PointRules.Hash(doc.ProjectInformation.UniqueId + "|" + doc.PathName);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Group3.EgressExporter", "settings", model, RevitPositions.Id(level) + "-" + RevitPositions.Id(phase) + ".json");
        }
        internal static ExportSettings Load(Document doc, Level level, Phase phase)
        {
            var path = PathFor(doc, level, phase);
            var settings = File.Exists(path) ? JsonFile.Read<ExportSettings>(path) : new ExportSettings {
                model_guid = doc.ProjectInformation.UniqueId, level_id = RevitPositions.Id(level), phase_id = RevitPositions.Id(phase) };
            PointRules.ValidateScope(settings, doc.ProjectInformation.UniqueId, RevitPositions.Id(level), RevitPositions.Id(phase));
            return settings;
        }
        internal static string Save(Document doc, Level level, Phase phase, ExportSettings settings)
        {
            PointRules.ValidateScope(settings, doc.ProjectInformation.UniqueId, RevitPositions.Id(level), RevitPositions.Id(phase));
            var path = PathFor(doc, level, phase);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            JsonFile.Write(path, settings);
            return path;
        }
    }
}
