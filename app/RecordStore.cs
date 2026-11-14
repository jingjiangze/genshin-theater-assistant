// ─────────────────────────────────────────────────────────────────────────────
// 记录库（SQLite）：把「每一帧看到了什么、决定了什么、花了多久」落盘。
//
// 为什么需要它：
//   · 决策质量只能靠事后对账（哪一帧给了什么建议、当时识别到什么）；日志是滚动的，留不住；
//   · 小模型微调需要「真实输入 + 当时的输出 + 人工判对错」三件套，否则只能靠合成样本；
//   · 「不影响前台看视频」需要能量化：如果录制本身很贵，就该关掉。
//
// 存储位置：%LOCALAPPDATA%\TheaterAssistant\records.db（WAL，退出时 checkpoint）。
// 写入策略：攒 32 条一个事务；每 tick 只做内存追加，不阻塞主循环。
// 保留策略：默认 30 天（--db-prune），体积按经验约 3–5MB/小时重度使用。
//
// 红线：这里只存「我们自己的输出与耗时」，不存屏幕位图（那等于录屏，体积与隐私都不合适）。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TheaterAssistant;

internal sealed class RecordStore : IDisposable
{
    public const int SchemaVersion = 1;
    private const int FlushEvery = 32;

    private readonly SqliteConnection _db;
    private readonly object _lock = new();
    private readonly List<TickRecord> _pending = new(FlushEvery);
    private DateTime _lastFlushUtc = DateTime.UtcNow;
    private bool _disposed;

    public string Path { get; }
    public string SessionId { get; private set; } = "";
    public long LastTickId { get; private set; }
    public int Written { get; private set; }
    public string Status { get; private set; } = "未打开";

    private RecordStore(string path, SqliteConnection db)
    {
        Path = path;
        _db = db;
    }

    /// <summary>打开（不存在则建）。失败返回 null —— 记录失败绝不影响识别主流程。</summary>
    public static RecordStore TryOpen(string path = null)
    {
        try
        {
            path ??= System.IO.Path.Combine(AppPaths.Dir, "records.db");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
            }.ToString();
            var db = new SqliteConnection(cs);
            db.Open();
            using (var pragma = db.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=3000;";
                pragma.ExecuteNonQuery();
            }
            var store = new RecordStore(path, db);
            store.Migrate();
            store.Status = "已打开 " + path;
            return store;
        }
        catch (Exception ex)
        {
            Console.WriteLine("记录库打开失败: " + ex.Message);
            return null;
        }
    }

    private void Migrate()
    {
        Exec(@"
CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT);
CREATE TABLE IF NOT EXISTS sessions(
  id TEXT PRIMARY KEY, started_utc TEXT NOT NULL, app_version TEXT, ocr_model TEXT,
  game_w INTEGER, game_h INTEGER, mode TEXT, note TEXT);
CREATE TABLE IF NOT EXISTS ticks(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  session TEXT NOT NULL, ts_utc TEXT NOT NULL, tick INTEGER,
  act INTEGER, stable_act INTEGER, kind TEXT, source TEXT,
  advice TEXT, guide TEXT,
  ocr_lines INTEGER, boxes INTEGER, frame_sha TEXT,
  ms_total INTEGER, ms_match INTEGER, ms_ocr INTEGER, ms_band INTEGER,
  skipped INTEGER NOT NULL DEFAULT 0);
CREATE INDEX IF NOT EXISTS ix_ticks_session ON ticks(session, id);
CREATE TABLE IF NOT EXISTS feedback(
  id INTEGER PRIMARY KEY AUTOINCREMENT, tick_id INTEGER NOT NULL,
  ts_utc TEXT NOT NULL, label TEXT NOT NULL, note TEXT);
CREATE INDEX IF NOT EXISTS ix_feedback_tick ON feedback(tick_id);
");
        Exec("INSERT OR REPLACE INTO meta(k,v) VALUES('schema', " + SchemaVersion + ");");
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>开一个会话（一次程序运行 = 一条）。</summary>
    public void BeginSession(string appVersion, string ocrModel, int gameW, int gameH, string mode)
    {
        try
        {
            SessionId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Guid.NewGuid().ToString("N")[..6];
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO sessions(id,started_utc,app_version,ocr_model,game_w,game_h,mode)
                                VALUES($id,$t,$v,$m,$w,$h,$mode)";
            cmd.Parameters.AddWithValue("$id", SessionId);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$v", appVersion ?? "");
            cmd.Parameters.AddWithValue("$m", ocrModel ?? "");
            cmd.Parameters.AddWithValue("$w", gameW);
            cmd.Parameters.AddWithValue("$h", gameH);
            cmd.Parameters.AddWithValue("$mode", mode ?? "");
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Status = "会话写入失败: " + ex.Message; }
    }

    /// <summary>收一条 tick（内存攒批，32 条落一次）。</summary>
    public void AddTick(TickRecord r)
    {
        if (_disposed) return;
        lock (_lock)
        {
            _pending.Add(r);
            // 攒满 32 条 或 距上次落盘 >5s 就写：被强杀（timeout/任务管理器）时最多丢 5 秒，
            // 否则轻量使用（<32 tick）会一条都不落盘——实测踩过：跑 9 秒导出 0 条。
            if (_pending.Count >= FlushEvery
                || (DateTime.UtcNow - _lastFlushUtc).TotalSeconds >= 5)
                FlushLocked();
        }
    }

    /// <summary>人工标错/标对（微调用的监督信号）。tickId 传 0 = 最近一条。</summary>
    public bool AddFeedback(long tickId, string label, string note = "")
    {
        try
        {
            if (tickId <= 0) tickId = LastTickId;
            if (tickId <= 0) return false;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO feedback(tick_id,ts_utc,label,note) VALUES($t,$ts,$l,$n)";
            cmd.Parameters.AddWithValue("$t", tickId);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$l", label ?? "");
            cmd.Parameters.AddWithValue("$n", note ?? "");
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex) { Status = "标注写入失败: " + ex.Message; return false; }
    }

    private void FlushLocked()
    {
        if (_pending.Count == 0) return;
        try
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO ticks(session,ts_utc,tick,act,stable_act,kind,source,advice,guide,
                                  ocr_lines,boxes,frame_sha,ms_total,ms_match,ms_ocr,ms_band,skipped)
                                VALUES($s,$ts,$tk,$a,$sa,$k,$src,$adv,$g,$ol,$bx,$sha,$mt,$mm,$mo,$mb,$sk);
                                SELECT last_insert_rowid();";
            // 逐条执行（SQLite 没有多行 VALUES 参数化的简洁写法；32 条在一个事务里已足够快）
            foreach (var r in _pending)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$s", r.Session ?? SessionId);
                cmd.Parameters.AddWithValue("$ts", r.TsUtc.ToString("o"));
                cmd.Parameters.AddWithValue("$tk", r.Tick);
                cmd.Parameters.AddWithValue("$a", (object)r.Act ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$sa", (object)r.StableAct ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$k", r.Kind ?? "");
                cmd.Parameters.AddWithValue("$src", r.Source ?? "");
                cmd.Parameters.AddWithValue("$adv", r.Advice ?? "");
                cmd.Parameters.AddWithValue("$g", r.Guide ?? "");
                cmd.Parameters.AddWithValue("$ol", r.OcrLines);
                cmd.Parameters.AddWithValue("$bx", r.Boxes);
                cmd.Parameters.AddWithValue("$sha", r.FrameSha ?? "");
                cmd.Parameters.AddWithValue("$mt", r.MsTotal);
                cmd.Parameters.AddWithValue("$mm", r.MsMatch);
                cmd.Parameters.AddWithValue("$mo", r.MsOcr);
                cmd.Parameters.AddWithValue("$mb", r.MsBand);
                cmd.Parameters.AddWithValue("$sk", r.Skipped ? 1 : 0);
                LastTickId = Convert.ToInt64(cmd.ExecuteScalar());
                Written++;
            }
            tx.Commit();
            _pending.Clear();
            _lastFlushUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Status = "写入失败: " + ex.Message;
            _pending.Clear();   // 丢这一批，绝不重试阻塞主循环
        }
    }

    /// <summary>清空待写并落盘（退出时调用）。</summary>
    public void Flush()
    {
        lock (_lock) FlushLocked();
    }

    /// <summary>
    /// 导出 JSONL（微调/对账用）。label 存在时一并带出：label=good 的样本可直接进 SFT，
    /// label=wrong_* 的进"待重标"队列 —— 训练脚本按 label 过滤。
    /// </summary>
    public int ExportJsonl(string outPath, bool onlyLabeled = false)
    {
        Flush();
        var n = 0;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
SELECT t.id,t.session,t.ts_utc,t.act,t.stable_act,t.kind,t.source,t.advice,t.guide,t.ocr_lines,t.boxes,
       t.ms_total,t.ms_ocr,f.label,f.note
FROM ticks t LEFT JOIN feedback f ON f.tick_id = t.id "
            + (onlyLabeled ? "WHERE f.id IS NOT NULL " : "")
            + "ORDER BY t.id";
        using var rd = cmd.ExecuteReader();
        using var w = new StreamWriter(outPath, false, new UTF8Encoding(false));
        while (rd.Read())
        {
            var rec = new
            {
                tick_id = rd.GetInt64(0),
                session = rd.GetString(1),
                ts_utc = rd.GetString(2),
                act = rd.IsDBNull(3) ? (int?)null : rd.GetInt32(3),
                stable_act = rd.IsDBNull(4) ? (int?)null : rd.GetInt32(4),
                kind = rd.IsDBNull(5) ? null : rd.GetString(5),
                source = rd.IsDBNull(6) ? null : rd.GetString(6),
                advice = rd.IsDBNull(7) ? null : rd.GetString(7),
                guide = rd.IsDBNull(8) ? null : rd.GetString(8),
                ocr_lines = rd.GetInt32(9),
                boxes = rd.GetInt32(10),
                ms_total = rd.GetInt32(11),
                ms_ocr = rd.GetInt32(12),
                label = rd.IsDBNull(13) ? null : rd.GetString(13),
                note = rd.IsDBNull(14) ? null : rd.GetString(14),
            };
            w.WriteLine(JsonSerializer.Serialize(rec, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            n++;
        }
        return n;
    }

    /// <summary>只留最近 N 天（默认 30）；返回删除行数。</summary>
    public int Prune(int keepDays = 30)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, keepDays)).ToString("o");
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM ticks WHERE ts_utc < $c; DELETE FROM feedback WHERE ts_utc < $c;";
            cmd.Parameters.AddWithValue("$c", cutoff);
            return cmd.ExecuteNonQuery();
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Flush();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch { }
        try { _db.Dispose(); } catch { }
    }

    internal sealed class TickRecord
    {
        public string Session;
        public DateTime TsUtc;
        public int Tick;
        public int? Act;
        public int? StableAct;
        public string Kind;
        public string Source;
        public string Advice;
        public string Guide;
        public int OcrLines;
        public int Boxes;
        public string FrameSha;
        public int MsTotal, MsMatch, MsOcr, MsBand;
        public bool Skipped;
    }
}
