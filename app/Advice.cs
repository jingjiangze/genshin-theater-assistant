// ─────────────────────────────────────────────────────────────────────────────
// 从 app/TheaterLogic.cs 抽出：Advice 是纯 DTO（指示的载体），
// 而 TheaterLogic 依赖 Matcher（System.Drawing）。WPF 版只需要 DTO，不该拖进 Drawing。
// ─────────────────────────────────────────────────────────────────────────────
using System.Collections.Generic;

namespace TheaterAssistant;

/// <summary>一条指示：标题 + 正文行 + 诊断串 + 幕次/界面类型。纯数据，无副作用。</summary>
internal sealed record Advice(string Header, List<string> Lines, string Diagnostics, int? Act = null, string Kind = null);
