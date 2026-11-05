using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Xml;

namespace Group3.EgressExporter
{
    // Lowercase field names deliberately match the shared v0.3 contract.
    [DataContract] public class Snapshot
    {
        [DataMember] public string schema_version = "0.3";
        [DataMember] public ProjectData project;
        [DataMember] public List<RoomData> rooms = new List<RoomData>();
        [DataMember] public List<DoorData> doors = new List<DoorData>();
        [DataMember] public List<DoorData> unresolved_doors = new List<DoorData>();
        [DataMember] public List<StairData> stairs = new List<StairData>();
        [DataMember] public List<StairData> unresolved_stairs = new List<StairData>();
        [DataMember] public List<CorridorData> corridors = new List<CorridorData>();
        [DataMember] public List<ObstacleData> obstacles = new List<ObstacleData>();
        [DataMember] public List<WarningData> warnings = new List<WarningData>();
    }
    [DataContract] public class ProjectData
    {
        [DataMember] public string model_name, model_guid, revit_version, phase, design_option;
        [DataMember] public string unit = "m", coordinate = "Revit 專案座標（Internal Origin）";
        [DataMember] public string extracted_at, model_version, phase_id, level_id;
        [DataMember] public string settings_file;
        [DataMember] public bool has_unsaved_changes;
        [DataMember] public LevelData level;
        [DataMember] public string model_guid_source = "ProjectInformation.UniqueId (另存副本可能相同)";
        [DataMember] public string export_scope = "host_document_single_level_main_model";
    }
    [DataContract] public class LevelData
    {
        [DataMember] public string name;
        [DataMember] public double elevation_m;
    }
    [DataContract] public class RoomData
    {
        [DataMember] public string element_id, unique_id, number, name, level;
        [DataMember] public string category = "Rooms";
        [DataMember] public double? area_m2;
        [DataMember] public List<double[]> boundary = new List<double[]>();
        [DataMember] public List<List<double[]>> boundary_loops = new List<List<double[]>>();
        [DataMember] public bool boundary_closed;
        [DataMember] public List<string> door_ids = new List<string>();
        [DataMember] public bool? is_habitable, is_circulation;
    }
    [DataContract] public class DoorData
    {
        [DataMember] public string element_id, unique_id;
        [DataMember] public List<string> room_ids = new List<string>();
        [DataMember] public double[] position;
        [DataMember] public double[] position_candidate;
        [DataMember] public string position_source;
        [DataMember] public bool? position_confirmed;
        [DataMember] public double? clear_width_m;
        [DataMember] public string clear_width_source;
    }
    [DataContract] public class StairData
    {
        [DataMember] public string element_id, unique_id, name;
        [DataMember] public List<double[]> footprint = new List<double[]>();
        [DataMember] public List<List<double[]>> footprint_loops = new List<List<double[]>>();
        [DataMember] public List<List<double[]>> run_paths = new List<List<double[]>>();
        [DataMember] public double[] entry_point;
        [DataMember] public string endpoint_type, endpoint_source;
        [DataMember] public bool? is_direct_stair;
    }
    [DataContract] public class CorridorData
    {
        [DataMember] public string element_id, unique_id, name;
        [DataMember] public List<double[]> centerline = new List<double[]>();
        [DataMember] public double? clear_width_m;
        [DataMember] public string clear_width_source;
        [DataMember] public bool? rooms_both_sides;
    }
    [DataContract] public class ObstacleData
    {
        [DataMember] public string element_id, unique_id, category, geometry_source;
        [DataMember] public List<double[]> footprint = new List<double[]>();
    }
    [DataContract] public class WarningData
    {
        [DataMember] public string element_id, code, message;
        [DataMember] public string severity = "warning";
    }
    // 把模型快照放進檢討網頁：產出的 html 不用網路、不用另外匯入，打開就是這個模型
    public static class ReportBuilder
    {
        public static string Inject(string template, string snapshotJson, string settingsJson)
        {
            // 跳脫 < ，模型裡的文字才不會提前結束 script
            return Fill(Fill(template, "/*G3_IMPORT*/null", snapshotJson.TrimStart('﻿').Replace("<", "\\u003c")), "/*G3_SETTINGS*/null", settingsJson);
        }
        static string Fill(string template, string marker, string value)
        {
            int at = template.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0 || template.IndexOf(marker, at + 1, StringComparison.Ordinal) >= 0) throw new ArgumentException("檢討網頁範本不正確，請重新建置外掛。");
            return template.Substring(0, at) + value + template.Substring(at + marker.Length);
        }
        // 使用者在擷取視窗選的專案設定，欄位對應 docs/data-contract.md 的專案設定
        public static string SettingsJson(string useGroup, int floorNumber, bool? fireResistant)
        {
            if (Array.IndexOf(UseGroups, useGroup) < 0) throw new ArgumentException("使用類組不正確：" + useGroup);
            return "{\"use_group\":\"" + useGroup + "\",\"use_group_set_by\":\"Revit 擷取視窗\",\"floor_number\":" +
                floorNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"below_ground\":" + (floorNumber < 0 ? "true" : "false") +
                ",\"fire_resistant\":" + (fireResistant.HasValue ? (fireResistant.Value ? "true" : "false") : "null") +
                ",\"tv_studio_with_audience\":null,\"corridor_serves_classroom\":null,\"duplex_housing\":false}";
        }
        public static readonly string[] UseGroups = { "A-1","A-2","B-1","B-2","B-3","B-4","C-1","C-2","D-1","D-2","D-3","D-4","D-5","E","F-1","F-2","F-3","F-4","G-1","G-2","G-3","H-1","H-2","I" };
    }
    public sealed class WarningGroup
    {
        public string code, message, severity;
        public int count;
        public List<string> element_ids = new List<string>();
    }
    // 缺漏清單的顯示方式：同一種問題只佔一列，要處理的排在提醒前面。JSON 內容不變。
    public static class WarningSummary
    {
        const string VariesNote = "（各件內容略有不同，詳見快照 warnings）";
        public static List<WarningGroup> Group(IEnumerable<WarningData> warnings)
        {
            var groups = new List<WarningGroup>();
            foreach (var w in warnings)
            {
                var group = groups.Find(g => g.code == w.code);
                if (group == null) { group = new WarningGroup { code = w.code, message = w.message, severity = w.severity }; groups.Add(group); }
                if (w.severity == "error") group.severity = "error";
                if (group.message != w.message && !group.message.EndsWith(VariesNote, StringComparison.Ordinal)) group.message += VariesNote;
                if (w.element_id != null && !group.element_ids.Contains(w.element_id)) group.element_ids.Add(w.element_id);
                group.count++;
            }
            groups.Sort((a, b) => {
                int rank = (a.severity == "error" ? 0 : 1).CompareTo(b.severity == "error" ? 0 : 1);
                return rank != 0 ? rank : string.CompareOrdinal(a.code, b.code);
            });
            return groups;
        }
    }
    // Settings use UniqueId, not transient display names. Null means unconfirmed.
    [DataContract] public class ExportSettings
    {
        [DataMember] public string model_guid, level_id, phase_id;
        [DataMember] public string clear_width_parameter = "Egress_ClearWidth";
        // 擷取視窗上次選的專案設定，下次同一模型同一層預先帶入
        [DataMember(EmitDefaultValue = false)] public string use_group;
        [DataMember(EmitDefaultValue = false)] public int? floor_number;
        [DataMember(EmitDefaultValue = false)] public bool? fire_resistant;
        [DataMember] public List<RoomSetting> rooms = new List<RoomSetting>();
        [DataMember] public List<DoorSetting> doors = new List<DoorSetting>();
        [DataMember] public List<StairSetting> stairs = new List<StairSetting>();
        [DataMember] public List<CorridorSetting> corridors = new List<CorridorSetting>();
    }
    [DataContract] public class RoomSetting
    {
        [DataMember] public string unique_id;
        [DataMember] public bool? is_habitable, is_circulation;
    }
    [DataContract] public class StairSetting
    {
        [DataMember] public string unique_id;
        [DataMember] public bool? is_direct_stair;
        // Explicit endpoint reference: a door insertion point or one end of a chosen curve.
        [DataMember] public string endpoint_unique_id, endpoint_type;
        [DataMember] public int curve_end;
        [DataMember] public PickedPointSetting picked_point;
    }
    [DataContract] public class PickedPointSetting
    {
        // Internal-Origin XYZ in metres. Fingerprint invalidates a point after geometry changes.
        [DataMember] public double[] point_m;
        [DataMember] public string geometry_fingerprint, reference_geometry_fingerprint, source;
    }
    [DataContract] public class DoorSetting
    {
        [DataMember] public string unique_id;
        [DataMember] public PickedPointSetting picked_point;
    }
    [DataContract] public class CorridorSetting
    {
        [DataMember] public string room_unique_id;
        [DataMember] public List<string> curve_unique_ids = new List<string>();
        [DataMember] public bool? rooms_both_sides;
    }
    public static class JsonFile
    {
        public static T Read<T>(string path)
        {
            using (var stream = File.OpenRead(path))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
        public static void Write<T>(string path, T value)
        {
            // Write and close completely before replacing a previous snapshot.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temp))
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream,
                    new System.Text.UTF8Encoding(false), false, true, "  "))
                    new DataContractJsonSerializer(typeof(T)).WriteObject(writer, value);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
