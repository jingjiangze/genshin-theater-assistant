#!/usr/bin/env python3
"""跨平台视频**文本语料**采集（基于成熟轮子 yt-dlp，不下载视频本体）。

  python tools/video-frames/ingest.py --platform bilibili --bvs BV1xx,BV1yy --limit 12 --out frames/ingest
  python tools/video-frames/ingest.py --platform youtube --query "Genshin Imaginarium Theater guide" --limit 8

用户明确：仅需要视频帧做模拟运行与对比，**不需要下载**。
画面帧由已验证的 hd.mjs（浏览器内 canvas、铺满全片）产出；本脚本只出文本元数据/字幕/章节/评论/弹幕。
PySceneDetect 从"必须依赖"降级为可选（B站流下载被 HTTP 567 限流，见 docs/VIDEO-TOOLING-AUDIT.md）。

依赖（CI 里 pip 装）：仅 yt-dlp。

输出（全部落 out 目录）：
  <video_id>/meta.json              标题/描述/章节/时长/平台/来源 URL
  <video_id>/subs.txt               字幕/评论/简介的纯文本（带通道标记）
  <video_id>-ops.json               操作候选，**格式与 --extract-ops 一致**，
                                    可直接喂给 TheaterAssistant.exe --consensus / --compare-ops
  ingest-summary.json              本批统计

安全边界：cookie 只从环境变量读，写入 out 下的临时 Netscape 文件，
          finally 里删除；不打日志、不进命令行、不进 artifact。
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from pathlib import Path

try:
    import yt_dlp
except ImportError:
    sys.exit("缺少 yt-dlp：pip install yt-dlp")


# ---------------- 操作候选分类（与 app/OpsExtract.cs 同口径） ----------------
COMBO = re.compile(r"[\u4e00-\u9fff A-Za-z]{1,4}\s*[EQeq](?:\s*[-—>→]\s*[\u4e00-\u9fff A-Za-z]{0,4}\s*[EQeq]){1,8}")
NOISE_EXACT = {
    "可选角色", "已选开幕与待命角色", "待命角色", "队伍配置", "敌人选择",
    "返回", "确认", "取消", "跳过", "继续", "神秘收获", "辉彩祝福", "伙伴",
    "助演", "演出回顾", "关卡详情", "难度选择", "点赞", "投币", "收藏", "关注",
}
NOISE_RE = re.compile(
    r"^(第\s*\d{1,2}\s*幕|Lv\.?\s*\d+|\d+[\.、]|[\d\W]{1,8}|"
    r"<[^>]+>|\{[^}]+\}|https?://\S+|\[[^\]]{0,12}\])$"
)
PROSE_RE = re.compile(r"(挑战|故事|演出|神秘房间|魔女|幻想与真实|永无休止|在故事|需要许多伙伴)[^\n]{0,40}。\s*$")

# UP 主简介里最有价值的是「逐幕配队」：`第1幕：一斗+爱诺+五郎+挂件`、
# `需要选出角色：5幕诺艾尔、7幕行秋+皇女、9幕那维莱特`。
# 这些长文本按"精确文本"去重永远形不成共识，必须抽成 (幕次, 角色) 结构化对。
ACT_LINE_RE = re.compile(r"^\s*(?:第)?\s*(\d{1,2})\s*幕[：:\s]*(.+)$")
ACT_SEG_RE = re.compile(r"(?:^|[、，,；;])\s*(?:第)?\s*(\d{1,2})\s*幕\s*([^、，,；;]{2,40})")
CHAR_SPLIT_RE = re.compile(r"[+＋、，,/\s]+")
BAD_CHAR_RE = re.compile(r"(挂件|主C|主c|辅助|后台|破盾|奶|盾|角色|尽量|剩下|需要|自备|偏强|偏弱|高频|组合|自带)")


def extract_act_roles(line: str) -> list[tuple[int, str]]:
    """从一行简介抽 [(幕次, 角色名)]；只保留像角色名的 2-6 字中文词。"""
    t = (line or "").strip()
    if not t:
        return []
    out: list[tuple[int, str]] = []
    pairs: list[tuple[str, str]] = []
    m = ACT_LINE_RE.match(t)
    if m:
        pairs.append((m.group(1), m.group(2)))
    else:
        pairs.extend((a, b) for a, b in ACT_SEG_RE.findall(t))
    for num, body in pairs:
        try:
            act = int(num)
        except ValueError:
            continue
        if not (1 <= act <= 12):
            continue
        for tok in CHAR_SPLIT_RE.split(body):
            tok = tok.strip()
            if not (2 <= len(tok) <= 8) or not re.fullmatch(r"[\u4e00-\u9fff]{2,8}", tok):
                continue
            if BAD_CHAR_RE.search(tok):
                continue
            out.append((act, tok))
    return out


_KNOWN_ROLES: list[str] = []


def load_known_roles() -> list[str]:
    """已知角色名（来自 data/substitutes.json），用于字幕/评论通道的宽松识别。"""
    global _KNOWN_ROLES
    if _KNOWN_ROLES:
        return _KNOWN_ROLES
    path = Path(__file__).resolve().parents[2] / "data" / "substitutes.json"
    try:
        doc = json.loads(path.read_text(encoding="utf-8-sig"))
        _KNOWN_ROLES = sorted(doc.get("characters", {}).keys(), key=len, reverse=True)
    except (OSError, ValueError):
        _KNOWN_ROLES = []
    return _KNOWN_ROLES


def classify(line: str) -> str | None:
    t = (line or "").strip()
    if len(t) < 4 or t in NOISE_EXACT or NOISE_RE.match(t) or PROSE_RE.search(t):
        return None
    if COMBO.search(t):
        return "combo"
    if re.search(r"(队伍|配队|助演|外援)[：:]", t):
        return "team"
    if re.search(r"(买|购入|选)[^\n。；]{2,30}", t):
        return "buy"
    if re.search(r"(先|优先|建议|需要|别|不要|第一时?间|开局)[^\n。；]{4,40}", t):
        return "action"
    return None


# 注意：Python 的 re 不支持 \p{P}（.NET/PCRE 才有），CI 实测 `re.error: bad escape \p`。
# 这里用显式标点集合，与 C# 端 Regex.Replace(..., @"[\s\p{P}]") 的口径保持一致。
PUNCT_RE = re.compile(
    r"[\s!-/:-@\[-`{-~"
    u"\u3000-\u303f\uff00-\uffef\u2000-\u206f\u00a1-\u00bf]"
)


def norm_key(text: str) -> str:
    return PUNCT_RE.sub("", text).lower()


# ---------------- cookie：env → 临时 Netscape 文件 → 用完删 ----------------
def cookie_file_from_env(out: Path) -> Path | None:
    raw = os.environ.get("BILI_COOKIE", "").strip()
    if not raw:
        return None
    p = out / ".yt-cookies.txt"
    lines = ["# Netscape HTTP Cookie File", ""]
    for kv in raw.split(";"):
        kv = kv.strip()
        if not kv or "=" not in kv:
            continue
        name, value = kv.split("=", 1)
        name, value = name.strip(), value.strip()
        if not name or not value:
            continue
        lines.append(f".bilibili.com\tTRUE\t/\tTRUE\t0\t{name}\t{value}")
    p.write_text("\n".join(lines) + "\n", encoding="utf-8")
    try:
        os.chmod(p, 0o600)
    except OSError:
        pass
    return p


# ---------------- 字幕 / VTT / SRT 文本提取 ----------------
TS_RE = re.compile(r"^\s*(\d{1,2}):(\d{2}):(\d{2})[.,](\d{1,3})\s*-->\s*", re.M)


def read_subtitle_text(path: Path) -> list[tuple[str, str]]:
    """返回 [(时间码, 文本)]；纯文本去重。"""
    try:
        raw = path.read_text(encoding="utf-8", errors="ignore")
    except OSError:
        return []
    out: list[tuple[str, str]] = []
    seen: set[str] = set()
    # 按空行分块；SRT/VTT 都是「序号 / 时间 --> 时间 / 文本」
    for block in re.split(r"\n\s*\n", raw):
        lines = [l.strip() for l in block.splitlines() if l.strip()]
        if not lines:
            continue
        ts = ""
        start = 0
        for i, l in enumerate(lines[:3]):
            m = TS_RE.match(l)
            if m:
                ts = f"{int(m.group(1)):02d}:{m.group(2)}:{m.group(3)}"
                start = i + 1
                break
        text = " ".join(lines[start:])
        text = re.sub(r"<[^>]+>|\{[^}]+\}", "", text).strip()
        if len(text) < 4:
            continue
        k = norm_key(text)
        if not k or k in seen:
            continue
        seen.add(k)
        out.append((ts, text))
    return out


def read_danmaku_text(path: Path) -> list[tuple[str, str]]:
    """B站弹幕 XML：yt-dlp 会把它当 danmaku 字幕写出来。"""
    try:
        raw = path.read_text(encoding="utf-8", errors="ignore")
    except OSError:
        return []
    out: list[tuple[str, str]] = []
    for m in re.finditer(r'<d p="([^"]*)"[^>]*>([^<]+)</d>', raw):
        text = m.group(2).strip()
        if len(text) < 4:
            continue
        try:
            t = float(m.group(1).split(",")[0])
            ts = f"{int(t // 60):02d}:{int(t % 60):02d}"
        except (ValueError, IndexError):
            ts = ""
        out.append((ts, text))
    return out


# ---------------- 主流程 ----------------
PURITY_EXCLUDE_RE = re.compile(r"(解说|实况|吐槽|整活|切片|混剪|合集|锐评|测评|分析|讲解|反应|弹幕版|二创)")
PURITY_STRONG_RE = re.compile(r"(纯净|无解说|无剪辑|原声|全程|完整|一条过|无删减)")


def classify_purity(title: str, duration, text_evidence: str) -> str:
    """纯净度分级（供"真实模拟"筛选输入源）。

    分级口径：
      excluded   标题明确是解说/剪辑类 —— 不要
      short      时长 < 3 分钟 —— 切片，不要
      pure       标题自称纯净 + 时长 >= 8 分钟 + 有文本证据
      likely     时长 >= 8 分钟且无排除词，但标题没自称纯净
      mixed      其余（时长 3–8 分钟，或既无纯净特征也无排除词）
    """
    t = title or ""
    if PURITY_EXCLUDE_RE.search(t):
        return "excluded"
    try:
        dur = float(duration or 0)
    except (TypeError, ValueError):
        dur = 0.0
    if 0 < dur < 180:
        return "short"
    strong = bool(PURITY_STRONG_RE.search(t))
    if dur >= 480 and strong and text_evidence == "AVAILABLE":
        return "pure"
    if dur >= 480:
        return "likely"
    return "mixed"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--platform", choices=["bilibili", "youtube"], default="bilibili")
    ap.add_argument("--query", default="幻想真境剧诗 月谕 攻略")
    ap.add_argument("--urls", default="", help="逗号分隔的直连 URL / BV 号（给了就跳过搜索）")
    ap.add_argument("--bvs", default="", help="B站 BV 号列表（来自 search.mjs；避开 bilisearch 的 412 风控）")
    ap.add_argument("--limit", type=int, default=12)
    ap.add_argument("--out", default="frames/ingest")
    # 不提供下载：本项目只需帧与文本元数据，画面帧由 hd.mjs 产出
    args = ap.parse_args()

    out = Path(args.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    cookie_path = cookie_file_from_env(out) if args.platform == "bilibili" else None

    # 实测（CI run 42169328064）：B站对 yt-dlp 的 `bilisearch:` 搜索返回 HTTP 412
    # Precondition Failed（风控），而本仓库的 Playwright DOM 搜索 search.mjs 已跑通 42+50 个视频。
    # 所以 B站搜索**不改由 yt-dlp 做**：搜索交给 search.mjs，yt-dlp 只接直连 BV/URL。
    # YouTube 的 ytsearch: 无此问题，仍可用。
    if args.bvs.strip():
        targets = [f"https://www.bilibili.com/video/{b.strip()}"
                   for b in args.bvs.split(",") if b.strip()]
    elif args.urls.strip():
        targets = [u.strip() for u in args.urls.split(",") if u.strip()]
    elif args.platform == "bilibili":
        sys.exit("B站必须提供 --bvs（由 search.mjs 产出）；yt-dlp 的 bilisearch: 会被 412 风控拦，见 docs/VIDEO-TOOLING-AUDIT.md")
    else:
        targets = [f"ytsearch{args.limit}:{args.query}"]

    ydl_base = {
        "quiet": True,
        "no_warnings": True,
        "ignoreerrors": True,
        "noplaylist": False,
        "socket_timeout": 30,
        "retries": 2,
        "sleep_interval_requests": 0.5,
        "extract_flat": "in_playlist",
        "playlistend": args.limit,
    }
    if cookie_path:
        ydl_base["cookiefile"] = str(cookie_path)

    summary = {
        "platform": args.platform, "query": args.query, "requested": args.limit,
        "videos": [], "generatedAt": time.strftime("%Y-%m-%d %H:%M:%S"),
    }

    try:
        # 1) 搜索 / 展开列表（extract_flat，不下载）
        entries: list[dict] = []
        with yt_dlp.YoutubeDL(ydl_base) as ydl:
            for t in targets:
                info = ydl.extract_info(t, download=False)
                if not info:
                    continue
                if info.get("_type") == "playlist" or "entries" in info:
                    for e in info.get("entries") or []:
                        if e:
                            entries.append(e)
                else:
                    entries.append(info)
        entries = entries[: args.limit]
        print(f"[info] 找到 {len(entries)} 个条目", file=sys.stderr)

        for e in entries:
            vid = str(e.get("id") or "")
            url = e.get("url") or e.get("webpage_url") or ""
            if not vid:
                continue
            safe = re.sub(r"[^0-9A-Za-z_-]", "_", vid)[:60]
            vdir = out / safe
            vdir.mkdir(parents=True, exist_ok=True)
            rec: dict = {"id": vid, "title": e.get("title") or "", "url": url, "platform": args.platform}

            # 2) 单视频：元数据 + 字幕 + 自动字幕 + 章节 + 评论
            opts = dict(ydl_base)
            opts.update({
                "extract_flat": False,
                "skip_download": True,
                "writeinfojson": True,
                "writesubtitles": True,
                "writeautomaticsub": True,
                "subtitleslangs": ["zh-Hans", "zh-CN", "zh", "en", "danmaku"],
                "getcomments": True,
                "outtmpl": str(vdir / "video.%(ext)s"),
                "sleep_interval": 1,
            })
            try:
                with yt_dlp.YoutubeDL(opts) as ydl:
                    info = ydl.extract_info(url, download=False) or {}
            except Exception as ex:
                print(f"[warn] {vid} 元数据失败: {type(ex).__name__}", file=sys.stderr)
                info = {}

            chapters = info.get("chapters") or []
            rec.update({
                "title": info.get("title") or rec["title"],
                "duration": info.get("duration"),
                "uploader": info.get("uploader"),
                "creator_id": info.get("uploader_id") or info.get("channel_id") or "",
                "description": (info.get("description") or "")[:4000],
                "chapters": [{"title": c.get("title"), "start": c.get("start_time"), "end": c.get("end_time")} for c in chapters],
                "subtitle_langs": sorted((info.get("subtitles") or {}).keys()),
                "auto_caption_langs": sorted((info.get("automatic_captions") or {}).keys())[:10],
            })
            (vdir / "meta.json").write_text(json.dumps(rec, ensure_ascii=False, indent=1), encoding="utf-8")

            # 3) 收集文本语料（字幕/弹幕/评论/简介/章节）
            texts: list[tuple[str, str, str]] = []   # (timecode, text, channel)
            for p in sorted(vdir.glob("*.srt")) + sorted(vdir.glob("*.vtt")):
                for ts, tx in read_subtitle_text(p):
                    texts.append((ts, tx, "subtitle"))
            for p in sorted(vdir.glob("*.xml")):
                for ts, tx in read_danmaku_text(p):
                    texts.append((ts, tx, "danmaku"))
            info_json = vdir / "video.info.json"
            if info_json.exists():
                try:
                    ij = json.loads(info_json.read_text(encoding="utf-8", errors="ignore"))
                    for c in (ij.get("comments") or [])[:500]:
                        tx = (c.get("text") or "").strip()
                        if len(tx) >= 4:
                            texts.append(("", tx, "comment"))
                except (OSError, ValueError):
                    pass
            if rec["description"]:
                for line in rec["description"].splitlines():
                    if len(line.strip()) >= 4:
                        texts.append(("", line.strip(), "description"))
            for c in chapters:
                t = (c.get("title") or "").strip()
                if len(t) >= 4:
                    s = c.get("start")
                    ts = f"{int(s // 60):02d}:{int(s % 60):02d}" if isinstance(s, (int, float)) else ""
                    texts.append((ts, t, "chapter"))

            subs_lines = [f"[{ch}|{ts}] {tx}" for ts, tx, ch in texts]
            (vdir / "subs.txt").write_text("\n".join(subs_lines), encoding="utf-8")

            # 4) 操作候选（格式与 --extract-ops 一致，可直接 --consensus）
            ops: list[dict] = []
            seen_ops: set[str] = set()
            for ts, tx, ch in texts:
                kind = classify(tx)
                if not kind:
                    continue
                k = kind + ":" + norm_key(tx)
                if k in seen_ops:
                    continue
                seen_ops.add(k)
                ops.append({
                    "kind": kind, "text": tx[:120], "act": 0,
                    "source": f"{args.platform}:{vid}" + (f"@{ts}" if ts else ""),
                    "channel": ch, "status": "CANDIDATE",
                    "creator_id": rec.get("creator_id") or "", "creator_name": rec.get("uploader") or "",
                })

            # 4b) 逐幕配队：把「第N幕：A+B+C」抽成 (幕次, 角色) 结构化条目。
            # 简介(description) 是 UP 主主动写的资料；字幕/评论/弹幕通道也参与，
            # 但保留 channel 出处，便于证据治理按来源可靠性区分。
            for ts, tx, ch in texts:
                pairs = extract_act_roles(tx)
                if not pairs and ch in ("subtitle", "comment", "danmaku"):
                    # 宽松通道：行内同时出现「第N幕」与已收录角色名时，也作为低权重候选
                    m = re.search(r"第\s*(\d{1,2})\s*幕", tx)
                    if m:
                        act = int(m.group(1))
                        if 1 <= act <= 12:
                            for role in load_known_roles():
                                if role in tx:
                                    pairs.append((act, role))
                for act, role in pairs:
                    label = f"第{act}幕 推荐 {role}"
                    k = "act_team:" + norm_key(label)
                    if k in seen_ops:
                        continue
                    seen_ops.add(k)
                    ops.append({
                        "kind": "act_team", "text": label, "act": act,
                        "source": f"{args.platform}:{vid}" + (f"@{ts}" if ts else ""),
                        "channel": ch, "status": "CANDIDATE",
                        "creator_id": rec.get("creator_id") or "", "creator_name": rec.get("uploader") or "",
                    })
            rec["act_team_pairs"] = sum(1 for o in ops if o["kind"] == "act_team")
            (out / f"{safe}-ops.json").write_text(
                json.dumps({
                    "note": f"{args.platform} 视频文本语料候选（yt-dlp 字幕/章节/评论/弹幕），等待自动证据与回归门禁",
                    "generatedAt": time.strftime("%Y-%m-%d %H:%M:%S"),
                    "bvid": vid, "frames": 0, "candidates": len(ops), "ops": ops,
                }, ensure_ascii=False, indent=1), encoding="utf-8")

            # 5) 不下载视频本体：用户明确「仅需要视频帧来做模拟运行与对比，不需要下载」。
            # 画面帧由已验证的 hd.mjs（浏览器内 canvas，铺满全片）产出；本脚本只出文本元数据。
            rec["scene_frames"] = 0
            rec["text_lines"] = len(texts)
            rec["ops_candidates"] = len(ops)
            # 来源形态标注：纯视频讲解（无简介/字幕/评论）不能提供文本证据，
            # 别让它每轮都被重采，也别把 0 候选误当"采集成功"。
            rec["text_evidence"] = (
                "NO_TEXT_EVIDENCE" if not texts else
                ("CHAPTER_ONLY" if all(ch == "chapter" for _, _, ch in texts) else "AVAILABLE")
            )
            # 「纯净流」最终分级：**必须放在 text_evidence 之后**（依赖它），
            # 否则 KeyError（本轮犯过：先算 purity 后赋值 text_evidence）。
            # 目的：为"真实模拟"挑高质量输入源，而不是拿解说/切片当素材。
            rec["purity"] = classify_purity(rec.get("title") or "", info.get("duration"), rec["text_evidence"])

            if rec["text_evidence"] in ("NO_TEXT_EVIDENCE", "CHAPTER_ONLY"):
                with (out / "no_text_videos.txt").open("a", encoding="utf-8") as f:
                    f.write(f"{vid}\t{rec['text_evidence']}\t{rec.get('creator_id') or ''}\n")
            summary["videos"].append(rec)
            print(f"[ok] {vid}: 文本 {len(texts)} 行 / 候选 {len(ops)} 条 / 纯净度 {rec['purity']}"
                  f" / {rec['text_evidence']}（未下载视频）", file=sys.stderr)
            time.sleep(1)   # 不在短时间内连续打接口

        (out / "ingest-summary.json").write_text(
            json.dumps(summary, ensure_ascii=False, indent=1), encoding="utf-8")
        total_text = sum(v.get("text_lines", 0) for v in summary["videos"])
        print(f"[done] 共 {len(summary['videos'])} 个视频 / {total_text} 行文本 → {out}", file=sys.stderr)
        # 零语料不能算成功（CI run 42171238930：YouTube 被反爬拦，6 个视频全 0 行却 exit 0）
        if not summary["videos"]:
            return 2
        if total_text == 0:
            print("[warn] 全部视频都没拿到文本语料：多半是平台要求登录 cookie（YouTube 反爬）"
                  "或缺少 yt-dlp-ejs；不绕过登录墙，请改用 --urls 提供可匿名访问的视频，"
                  "或自行提供该平台的 cookie。", file=sys.stderr)
            return 3
        return 0

    finally:
        # cookie 临时文件绝不留在产物里
        if cookie_path and cookie_path.exists():
            try:
                cookie_path.unlink()
            except OSError:
                pass


if __name__ == "__main__":
    raise SystemExit(main())
