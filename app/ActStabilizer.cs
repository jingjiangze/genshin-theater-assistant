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

    public int? Committed => _committed;

    public int? Push(int? observed)
    {
        if (!observed.HasValue)
        {
            // 没看到幕次：保持已确认的结果，避免 unknown/act 来回抖
            _pending = null;
            _pendingCount = 0;
            return _committed;
        }

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
