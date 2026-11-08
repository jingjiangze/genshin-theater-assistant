namespace TheaterAssistant;

/// <summary>
/// 幕次时序稳定器：把"逐帧识别出来的幕次"变成"可信的当前幕次"。
///
/// 实测背景（--replay 100 帧）：原始序列出现 `10 → 7`、`7 → 9`、`8 → 4` 这类倒退，
/// 以及 13 次状态抖动 —— 主要是条带里混进幕次列表/攻略贴字导致的单帧误读。
///
/// 规则（可解释、无模型）：
///   · 正常推进（当前 +1）或首次识别 → 立刻采纳（不引入延迟）；
///   · 倒退、跳级、或与"没有观测到幕次"交替 → 必须连续观察到 MinConfirm 次才采纳；
///   · 没观测到幕次 → 保持上一次结果（不闪回 unknown）。
/// </summary>
internal sealed class ActStabilizer
{
    private int? _committed;
    private int? _pending;
    private int _pendingCount;
    private bool _pendingFromNone;

    /// <summary>需要连续确认的次数（默认 2）。</summary>
    public int MinConfirm { get; set; } = 2;

    /// <summary>被拦下的候选次数（诊断用）。</summary>
    public int Rejected { get; private set; }

    /// <summary>连续"没观测到幕次"的帧数（诊断用）。</summary>
    public int ConsecutiveMisses { get; private set; }

    /// <summary>
    /// 连续未识别的**持续时间**（毫秒）——弃权判定的真正依据。
    ///
    /// 为什么不用"帧数"：帧数阈值依赖 tick 频率。实测本程序 tick=1000ms，
    /// 3 帧 = 3 秒；但切界面、打 boss、结算贴图时幕次标签消失 5–15 秒是常态，
    /// 3 秒就弃权会把大量"其实还在同一幕"的画面判成未知。
    /// 用时间则与 tick 频率解耦，换设备/改扫描间隔都不用重调。
    /// </summary>
    public int MissedMilliseconds { get; private set; }

    /// <summary>弃权阈值（毫秒）。默认 10 秒——覆盖"幕次标签短暂消失"的常见场景。</summary>
    public int AbstainAfterMilliseconds { get; set; } = 10000;

    private DateTime? _firstMissAt;

    /// <summary>
    /// 连续未识别帧数达到该阈值时，调用方应**弃权**（明确说"暂不判断"），
    /// 而不是继续沿用旧幕次给指示。实测约 25~30% 帧属于沿用，长期沿用会给出过时指令。
    /// </summary>
    public int AbstainAfterMisses { get; set; } = 3;

    /// <summary>是否该弃权：以**持续时间**为准；帧数阈值保留为辅助上限（防极端慢 tick）。</summary>
    public bool ShouldAbstain =>
        MissedMilliseconds >= Math.Max(1000, AbstainAfterMilliseconds)
        || ConsecutiveMisses >= Math.Max(AbstainAfterMisses, 30);

    public int? Committed => _committed;

    public int? Push(int? observed)
    {
        if (!observed.HasValue)
        {
            // 没观测到幕次：保持已确认结果，但**不清零**候选计数 ——
            // 条带 OCR 经常漏一帧，若清零，`9, 漏, 9` 就永远确认不了（CI 断言抓到过）。
            ConsecutiveMisses++;
            _firstMissAt ??= DateTime.UtcNow;
            MissedMilliseconds = (int)(DateTime.UtcNow - _firstMissAt.Value).TotalMilliseconds;
            return _committed;
        }

        ConsecutiveMisses = 0;
        MissedMilliseconds = 0;
        _firstMissAt = null;
        var act = observed.Value;

        if (_committed == act)
        {
            _pending = null;
            _pendingCount = 0;
            return _committed;
        }

        bool plausible = !_committed.HasValue || act == _committed.Value + 1;

        if (_pending == act)
        {
            _pendingCount++;
        }
        else
        {
            _pending = act;
            _pendingCount = 1;
            _pendingFromNone = !_committed.HasValue;
        }

        if (plausible || _pendingCount >= Math.Max(1, MinConfirm))
        {
            _committed = act;
            _pending = null;
            _pendingCount = 0;
            return _committed;
        }

        Rejected++;
        return _committed;
    }

    /// <summary>新一轮录像/新一局时重置。</summary>
    public void Reset()
    {
        _committed = null;
        _pending = null;
        _pendingCount = 0;
        Rejected = 0;
    }
}
