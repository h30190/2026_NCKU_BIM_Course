using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Group3.EgressExporter
{
    public static class PointRules
    {
        public static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
        public static bool IsPoint(double[] point) => point != null && point.Length == 3 && point.All(Finite);
        public static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
        public static string Fingerprint(IEnumerable<double[]> vertices, string identity)
        {
            var points = vertices.ToList();
            if (points.Count == 0 || points.Any(p => !IsPoint(p))) throw new ArgumentException("無有效幾何，不能儲存定位。");
            return Hash(identity + "|" + string.Join(";", points.Select(p => string.Join(",", p.Select(n =>
                Math.Round(n, 6).ToString("F6", CultureInfo.InvariantCulture)))).OrderBy(s => s, StringComparer.Ordinal)));
        }
        public static void ValidatePoint(PickedPointSetting saved, string fingerprint, double levelElevation,
            double[] min, double[] max, double margin)
        {
            if (saved == null || !IsPoint(saved.point_m)) throw new ArgumentException("定位點必須是有效 XYZ 公尺座標。");
            if (string.IsNullOrWhiteSpace(saved.geometry_fingerprint) || saved.geometry_fingerprint != fingerprint)
                throw new ArgumentException("元件幾何已變更或缺少核對資訊，請重新設定位置。");
            if (!Finite(levelElevation) || Math.Abs(saved.point_m[2] - levelElevation) > 0.001)
                throw new ArgumentException("定位點不是本樓層平面，請在正確樓層重新設定。");
            if (!IsPoint(min) || !IsPoint(max) || !Finite(margin) || margin < 0 ||
                Enumerable.Range(0, 3).Any(i => min[i] > max[i])) throw new ArgumentException("元件範圍無效。");
            for (int i = 0; i < 2; i++)
                if (saved.point_m[i] < min[i] - margin || saved.point_m[i] > max[i] + margin)
                    throw new ArgumentException("定位點離元件範圍太遠，請核對選取的元件與點。");
        }
        public static ExportSettings Normalize(ExportSettings settings)
        {
            if (settings == null) throw new ArgumentException("設定檔不能為 null。");
            settings.rooms = settings.rooms ?? new List<RoomSetting>();
            settings.doors = settings.doors ?? new List<DoorSetting>();
            settings.stairs = settings.stairs ?? new List<StairSetting>();
            settings.corridors = settings.corridors ?? new List<CorridorSetting>();
            return settings;
        }
        public static void ValidateScope(ExportSettings settings, string model, string level, string phase)
        {
            Normalize(settings);
            if (settings.model_guid != model || settings.level_id != level || settings.phase_id != phase)
                throw new ArgumentException("設定檔的模型、樓層或階段不符。");
        }
        public static void SetDoor(ExportSettings settings, DoorSetting item)
        {
            Normalize(settings);
            settings.doors.RemoveAll(d => d != null && d.unique_id == item.unique_id);
            settings.doors.Add(item);
        }
        public static void SetStair(ExportSettings settings, StairSetting item)
        {
            Normalize(settings);
            settings.stairs.RemoveAll(s => s != null && s.unique_id == item.unique_id);
            settings.stairs.Add(item);
        }
    }
}
