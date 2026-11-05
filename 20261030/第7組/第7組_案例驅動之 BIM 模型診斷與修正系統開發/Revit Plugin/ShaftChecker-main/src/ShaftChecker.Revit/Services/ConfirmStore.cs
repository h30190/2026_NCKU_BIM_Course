using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using ShaftChecker.Revit.Bridge;

namespace ShaftChecker.Revit.Services
{
    /// <summary>
    /// 使用者對「這個空間是不是管道間」的決定（"是" / "否"），存在 %AppData%\ShaftChecker\confirmations.json。
    /// 不寫進模型，所以連結模型（唯讀）裡的房間也能確認。換電腦需要重新確認。
    /// </summary>
    public static class ConfirmStore
    {
        private static readonly object Gate = new object();

        private static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ShaftChecker", "confirmations.json");
            }
        }

        public static string DocKey(Document doc)
        {
            return string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName;
        }

        public static string RoomKey(string sourceName, string uniqueId)
        {
            return (sourceName ?? "") + "|" + uniqueId;
        }

        /// <summary>這個專案目前所有的決定：房間鍵 → "是"/"否"。</summary>
        public static Dictionary<string, string> GetAll(string docKey)
        {
            var result = new Dictionary<string, string>();
            lock (Gate)
            {
                object docObj;
                var all = Load();
                if (!all.TryGetValue(docKey, out docObj)) return result;

                var map = docObj as Dictionary<string, object>;
                if (map == null) return result;
                foreach (var kv in map)
                    if (kv.Value != null) result[kv.Key] = Convert.ToString(kv.Value);
            }
            return result;
        }

        /// <summary>value 為 "是" 或 "否"；null 代表清除決定。</summary>
        public static void Set(string docKey, string roomKey, string value)
        {
            lock (Gate)
            {
                var all = Load();
                object docObj;
                Dictionary<string, object> map = null;
                if (all.TryGetValue(docKey, out docObj)) map = docObj as Dictionary<string, object>;
                if (map == null)
                {
                    map = new Dictionary<string, object>();
                    all[docKey] = map;
                }

                if (value == null) map.Remove(roomKey);
                else map[roomKey] = value;

                string dir = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, MiniJson.Serialize(all), new UTF8Encoding(false));
            }
        }

        private static Dictionary<string, object> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new Dictionary<string, object>();
                return MiniJson.ParseObject(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch (Exception)
            {
                return new Dictionary<string, object>();
            }
        }
    }
}
