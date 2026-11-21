using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OC2Tweaks
{
    internal sealed class BanRecord
    {
        internal readonly string SteamId;
        internal readonly string Name;
        internal readonly DateTime BannedUtc;
        internal BanRecord(string id, string name, DateTime utc)
        {
            SteamId = id;
            Name = name ?? "";
            BannedUtc = utc;
        }
    }

    // Independent of Unity so persistence and failure handling can be verified offline.
    internal sealed class BanStore
    {
        // The on-disk format marker, not the mod name: loading matches it exactly, so
        // renaming it would make every existing ban list fail to parse. Left as written.
        private const string Header = "OC2LobbyTools bans v1";
        private readonly string path;
        private readonly object gate = new object();
        private Dictionary<string, BanRecord> records = new Dictionary<string, BanRecord>();
        internal bool Ready { get; private set; }
        internal BanStore(string path) { this.path = path; }

        internal static bool ValidId(string id)
        {
            ulong value;
            return id != null && ulong.TryParse(id, NumberStyles.None,
                CultureInfo.InvariantCulture, out value) && value > 0 &&
                value.ToString(CultureInfo.InvariantCulture) == id;
        }

        internal void Load()
        {
            lock (gate)
            {
                Ready = false;
                var loaded = new Dictionary<string, BanRecord>();
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                    if (lines.Length == 0 || lines[0] != Header)
                        throw new IOException("Không nhận dạng được tệp danh sách cấm. Tệp gốc đã được giữ nguyên.");
                    for (int i = 1; i < lines.Length; i++)
                    {
                        if (lines[i].Length == 0) continue;
                        string[] parts = lines[i].Split('\t');
                        DateTime utc;
                        if (parts.Length != 3 || !ValidId(parts[0]) ||
                            !DateTime.TryParseExact(parts[2], "o", CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out utc) || utc.Kind != DateTimeKind.Utc)
                            throw new IOException("Dữ liệu cấm không hợp lệ tại dòng " + (i + 1) + ". Tệp gốc đã được giữ nguyên.");
                        string name = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(parts[1]));
                        loaded[parts[0]] = new BanRecord(parts[0], name, utc);
                    }
                }
                records = loaded;
                Ready = true;
            }
        }

        internal bool Contains(string id)
        {
            lock (gate) { return id != null && records.ContainsKey(id); }
        }

        internal BanRecord[] Snapshot()
        {
            lock (gate)
            {
                var result = new List<BanRecord>(records.Values);
                result.Sort(delegate(BanRecord a, BanRecord b) { return b.BannedUtc.CompareTo(a.BannedUtc); });
                return result.ToArray();
            }
        }

        internal void Ban(string id, string name)
        {
            if (!ValidId(id)) throw new ArgumentException("Cần mã tài khoản Steam hợp lệ để lưu lệnh cấm.");
            lock (gate)
            {
                RequireReady();
                var next = new Dictionary<string, BanRecord>(records);
                next[id] = new BanRecord(id, name, DateTime.UtcNow);
                Persist(next);
                records = next; // Only commit memory after the durable write succeeds.
            }
        }

        internal void Unban(string id)
        {
            lock (gate)
            {
                RequireReady();
                var next = new Dictionary<string, BanRecord>(records);
                if (!next.Remove(id)) return;
                Persist(next);
                records = next;
            }
        }

        private void RequireReady()
        {
            if (!Ready) throw new InvalidOperationException("Không tải được danh sách cấm. Hãy sửa tệp rồi khởi động lại trò chơi.");
        }

        private void Persist(Dictionary<string, BanRecord> next)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var ids = new List<string>(next.Keys);
                ids.Sort(StringComparer.Ordinal);
                var content = new StringBuilder(Header).Append('\n');
                foreach (string id in ids)
                {
                    BanRecord record = next[id];
                    content.Append(id).Append('\t')
                        .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(record.Name))).Append('\t')
                        .Append(record.BannedUtc.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
                }
                File.WriteAllText(temporary, content.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
