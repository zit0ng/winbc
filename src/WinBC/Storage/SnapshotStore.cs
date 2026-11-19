using System.Globalization;
using WinBC.Models;

namespace WinBC.Storage;

/// <summary>
/// 快照仓库：每份快照一个 JSON 文件，另有独立的 rolling.json 滚动快照。
/// 目录：%LOCALAPPDATA%\WinBC\snapshots
/// </summary>
public sealed class SnapshotStore
{
    private const string RollingFileName = "_rolling.json";
    private const string PinsFileName = "_pins.json";
    private readonly object _gate = new();

    public string RootDir { get; }

    public SnapshotStore(Settings settings)
    {
        RootDir = settings.SnapshotDir;
        try { Directory.CreateDirectory(RootDir); }
        catch (Exception ex) { Util.Log.Error("创建快照目录失败", ex); }
    }

    public int Count => ListSnapshots().Count;

    // ── 置顶 ─────────────────────────────────────────────

    private string PinsPath => Path.Combine(RootDir, PinsFileName);

    public HashSet<string> GetPinnedIds()
    {
        try
        {
            if (!File.Exists(PinsPath)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(PinsPath);
            var ids = Util.AppJson.Deserialize<List<string>>(json);
            return new HashSet<string>(ids ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool IsPinned(string id) => GetPinnedIds().Contains(id);

    public void SetPinned(string id, bool pinned)
    {
        try
        {
            var ids = GetPinnedIds();
            if (pinned) ids.Add(id);
            else ids.Remove(id);

            Directory.CreateDirectory(RootDir);
            var tmp = PinsPath + ".tmp";
            File.WriteAllText(tmp, Util.AppJson.Serialize(ids.ToList()));
            File.Move(tmp, PinsPath, true);
        }
        catch (Exception ex)
        {
            Util.Log.Error("保存置顶状态失败", ex);
        }
    }

    /// <summary>删掉所有历史记录（保留 _pins.json 之外的全部快照）。</summary>
    public void DeleteAll()
    {
        try
        {
            if (!Directory.Exists(RootDir)) return;
            foreach (var f in Directory.GetFiles(RootDir, "*.json"))
            {
                var name = Path.GetFileName(f);
                if (name.StartsWith("_", StringComparison.Ordinal)) continue;
                try { File.Delete(f); } catch { }
            }
            try { if (File.Exists(PinsPath)) File.Delete(PinsPath); } catch { }
            Util.Log.Info("已清除全部历史记录");
        }
        catch (Exception ex)
        {
            Util.Log.Error("清除历史记录失败", ex);
        }
    }

    /// <summary>列出所有正式快照（不含滚动快照）。置顶的排最前，其余按时间倒序。</summary>
    public IReadOnlyList<Snapshot> ListSnapshots()
    {
        var list = new List<Snapshot>();
        try
        {
            if (!Directory.Exists(RootDir)) return list;
            foreach (var file in Directory.GetFiles(RootDir, "*.json"))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("_", StringComparison.Ordinal)) continue;
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                var snap = TryRead(file);
                if (snap is not null) list.Add(snap);
            }
        }
        catch (Exception ex)
        {
            Util.Log.Error("枚举快照失败", ex);
        }

        var pins = GetPinnedIds();
        return list
            .OrderByDescending(s => pins.Contains(s.Id))
            .ThenByDescending(s => s.CreatedAt)
            .ToList();
    }

    public Snapshot? Load(string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            var path = Path.Combine(RootDir, id + ".json");
            return File.Exists(path) ? TryRead(path) : null;
        }
        catch (Exception ex)
        {
            Util.Log.Error($"读取快照 {id} 失败", ex);
            return null;
        }
    }

    public string Save(Snapshot snapshot)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(RootDir);
                if (string.IsNullOrWhiteSpace(snapshot.Id))
                    snapshot.Id = MakeId(snapshot.CreatedAt, snapshot.Reason);

                // 同一秒内重复采集时避免覆盖
                var path = Path.Combine(RootDir, snapshot.Id + ".json");
                int suffix = 1;
                while (File.Exists(path))
                {
                    snapshot.Id = MakeId(snapshot.CreatedAt, snapshot.Reason) + "_" + suffix++;
                    path = Path.Combine(RootDir, snapshot.Id + ".json");
                }

                WriteAtomic(path, snapshot);
                Util.Log.Info($"快照已保存 {snapshot.Id}（应用 {snapshot.AppCount}，标签 {snapshot.TabCount}）");
                return snapshot.Id;
            }
            catch (Exception ex)
            {
                Util.Log.Error("保存快照失败", ex);
                return snapshot.Id;
            }
        }
    }

    public void SaveRolling(Snapshot snapshot)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(RootDir);
                if (string.IsNullOrWhiteSpace(snapshot.Id))
                    snapshot.Id = "_rolling";
                WriteAtomic(Path.Combine(RootDir, RollingFileName), snapshot);
            }
            catch (Exception ex)
            {
                Util.Log.Error("保存滚动快照失败", ex);
            }
        }
    }

    public Snapshot? LoadRolling()
    {
        try
        {
            var path = Path.Combine(RootDir, RollingFileName);
            return File.Exists(path) ? TryRead(path) : null;
        }
        catch (Exception ex)
        {
            Util.Log.Error("读取滚动快照失败", ex);
            return null;
        }
    }

    public void Delete(string id)
    {
        DeleteCore(id);
    }

    private void DeleteCore(string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var path = Path.Combine(RootDir, id + ".json");
            if (File.Exists(path)) File.Delete(path);

            // 顺手把置顶记录也清掉，否则会在 _pins.json 里留下僵尸 id
            var pins = GetPinnedIds();
            if (pins.Remove(id))
            {
                var tmp = PinsPath + ".tmp";
                File.WriteAllText(tmp, Util.AppJson.Serialize(pins.ToList()));
                File.Move(tmp, PinsPath, true);
            }
        }
        catch (Exception ex)
        {
            Util.Log.Error($"删除快照 {id} 失败", ex);
        }
    }

    /// <summary>
    /// 按"保留天数"清理，再用"最多份数"兜底。
    /// <paramref name="retentionDays"/> ≤ 0 表示不按天数删。
    /// </summary>
    public void Prune(int maxSnapshots, int retentionDays = 0)
    {
        try
        {
            var all = ListSnapshots();
            if (all.Count == 0) return;

            var doomed = new List<Snapshot>();

            if (retentionDays > 0)
            {
                var cutoff = DateTimeOffset.Now.AddDays(-retentionDays);
                doomed.AddRange(all.Where(s => s.CreatedAt < cutoff));
                if (doomed.Count > 0)
                    Util.Log.Info($"按 {retentionDays} 天策略清理 {doomed.Count} 份过期快照");
            }

            if (maxSnapshots > 0)
            {
                var doomedIds = new HashSet<string>(doomed.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
                var remaining = all
                    .Where(s => !doomedIds.Contains(s.Id))
                    .OrderByDescending(s => s.CreatedAt)
                    .ToList();

                if (remaining.Count > maxSnapshots)
                {
                    var extra = remaining.Skip(maxSnapshots).ToList();
                    doomed.AddRange(extra);
                    Util.Log.Info($"超出数量上限，再清理 {extra.Count} 份");
                }
            }

            foreach (var old in doomed)
                DeleteCore(old.Id);
        }
        catch (Exception ex)
        {
            Util.Log.Error("清理旧快照失败", ex);
        }
    }

    /// <summary>取"上一次关机前"最合适的快照：优先关机/注销快照，其次最新快照，最后滚动快照。</summary>
    public Snapshot? GetLatestRestorable()
    {
        var all = ListSnapshots();
        var power = all.FirstOrDefault(s => s.Reason is "shutdown" or "logoff");
        if (power is not null) return power;
        if (all.Count > 0) return all[0];
        return LoadRolling();
    }

    private static string MakeId(DateTimeOffset when, string reason)
    {
        var tag = string.IsNullOrWhiteSpace(reason) ? "snap" : reason;
        return $"{when.LocalDateTime:yyyyMMdd_HHmmss}_{tag}";
    }

    private static void WriteAtomic(string path, Snapshot snapshot)
    {
        var tmp = path + ".tmp";
        var json = Util.AppJson.Serialize(snapshot);
        File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    private static Snapshot? TryRead(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return Util.AppJson.Deserialize<Snapshot>(json);
        }
        catch (Exception ex)
        {
            Util.Log.Warn($"跳过损坏的快照文件 {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }
}
