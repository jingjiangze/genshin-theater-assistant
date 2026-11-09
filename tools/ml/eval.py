"""剧诗小模型评测：在留出集上跑生成，按任务分项打分。

四项总体指标 + 分任务的「决策效果」判据：
  · t1_now（现在做）：答案来自 playbook，是唯一的 → 看**精确匹配率**（空白归一后全文一致）；
  · t2_team（配队） ：看**队名覆盖率**（金标里的角色名，回答覆盖多少）；
  · t3_qa （问答） ：实体召回 + 长度。
门槛写在这里，评不过就不准上线：
  action_rate ≥0.8｜t1_exact ≥0.7｜act_match ≥0.8｜length_ok ≥0.9

用法：
    .mlvenv/Scripts/python.exe tools/ml/eval.py --base Qwen/Qwen2.5-0.5B-Instruct \
        --adapter out/lora-theater --data data/ml
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys

import torch
from peft import PeftModel
from transformers import AutoModelForCausalLM, AutoTokenizer

ACT_RE = re.compile(r"第\s*(\d{1,2})\s*幕")
ACTION_HINTS = ("现在做", "优先", "选第", "依据", "先", "别")

# 门槛：低于任何一条，评测判 FAIL（exit 1）。
# 门槛打在「决策任务」（t1 现在 做 / t2 配队）上——生产架构里 t3 语料问答走 ○语料检索层，
# 模型只在量化层与语料都拿不准时补位，所以 t3 的退化输出不影响产品决策，只作信息记录。
GATES = {"action_rate": 0.8, "t1_exact": 0.7, "act_match_rate": 0.8, "length_ok_rate": 0.9}


def load_jsonl(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(l) for l in f if l.strip()]


def norm(t: str) -> str:
    return re.sub(r"\s+", "", t or "")


def entities(text: str) -> set:
    """粗粒度实体：中文 2-4 字片段（角色名多为此形）+ 数字。"""
    out = set(re.findall(r"[\u4e00-\u9fa5]{2,4}", text))
    out |= set(re.findall(r"\d{1,3}", text))
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="Qwen/Qwen2.5-0.5B-Instruct")
    ap.add_argument("--adapter", default="out/lora-theater")
    ap.add_argument("--data", default="data/ml")
    ap.add_argument("--limit", type=int, default=0, help="0=全量")
    ap.add_argument("--max-new", type=int, default=120)
    ap.add_argument("--device", default="auto")
    ap.add_argument("--tasks", default="t1_now,t2_team",
                    help="参与评测的任务；门槛只算这些。all = 三类都跑")
    args = ap.parse_args()

    tasks = None if args.tasks == "all" else set(args.tasks.split(","))

    device = args.device
    if device == "auto":
        device = "xpu" if getattr(torch, "xpu", None) and torch.xpu.is_available() else (
            "cuda" if torch.cuda.is_available() else "cpu")
    dtype = torch.bfloat16 if device == "xpu" else (torch.float16 if device == "cuda" else torch.float32)

    if os.path.isdir(args.adapter) and os.path.exists(os.path.join(args.adapter, "adapter_config.json")):
        # LoRA 产物：base + adapter
        tok = AutoTokenizer.from_pretrained(args.base, trust_remote_code=True)
        model = AutoModelForCausalLM.from_pretrained(args.base, dtype=dtype, trust_remote_code=True)
        model = PeftModel.from_pretrained(model, args.adapter)
    elif os.path.isdir(args.adapter) and os.path.exists(os.path.join(args.adapter, "config.json")):
        # 全参微调产物：目录本身就是完整模型
        model = AutoModelForCausalLM.from_pretrained(args.adapter, dtype=dtype, trust_remote_code=True)
        tok = AutoTokenizer.from_pretrained(args.adapter, trust_remote_code=True)
    else:
        tok = AutoTokenizer.from_pretrained(args.base, trust_remote_code=True)
        model = AutoModelForCausalLM.from_pretrained(args.base, dtype=dtype, trust_remote_code=True)
    model.to(device).eval()

    rows = load_jsonl(os.path.join(args.data, "theater-sft-val.jsonl"))
    if args.limit:
        rows = rows[: args.limit]

    n = ok_action = ok_act = ok_len = 0
    ent_hit = ent_all = 0
    t1n = t1hit = 0
    t2n = t2hit = 0
    samples = []

    for r in rows:
        if tasks is not None and r.get("task", "") not in tasks:
            continue
        msgs = r["messages"]
        prompt = tok.apply_chat_template(msgs[:-1], tokenize=False, add_generation_prompt=True)
        ids = tok(prompt, return_tensors="pt").to(device)
        with torch.no_grad():
            out = model.generate(**ids, max_new_tokens=args.max_new, do_sample=False,
                                 pad_token_id=tok.eos_token_id)
        text = tok.decode(out[0][ids["input_ids"].shape[1]:], skip_special_tokens=True).strip()
        gold = msgs[-1]["content"]
        task = r.get("task", "")

        n += 1
        if any(h in text for h in ACTION_HINTS):
            ok_action += 1
        gold_act = ACT_RE.search(gold)
        pred_act = ACT_RE.search(text)
        if not gold_act or (pred_act and pred_act.group(1) == gold_act.group(1)):
            ok_act += 1
        if 0 < len(text) <= 120:
            ok_len += 1
        ge, te = entities(gold), entities(text)
        if ge:
            ent_all += len(ge)
            ent_hit += len(ge & te)

        if task == "t1_now":
            t1n += 1
            if norm(text) == norm(gold):
                t1hit += 1
        elif task == "t2_team":
            t2n += 1
            names = re.findall(r"[\u4e00-\u9fa5]{2,4}（", gold)
            names = [x[:-1] for x in names]
            if names:
                t2hit += sum(1 for x in names if x in text) / len(names)

        if len(samples) < 8:
            samples.append({"task": task, "user": msgs[1]["content"],
                            "gold": gold[:160], "pred": text[:160]})

    report = {
        "n": n,
        "base": args.base,
        "adapter": args.adapter,
        "device": device,
        "action_rate": round(ok_action / n, 3) if n else 0,
        "act_match_rate": round(ok_act / n, 3) if n else 0,
        "length_ok_rate": round(ok_len / n, 3) if n else 0,
        "entity_recall": round(ent_hit / ent_all, 3) if ent_all else 0,
        "t1_exact": round(t1hit / t1n, 3) if t1n else None,
        "t2_name_cover": round(t2hit / t2n, 3) if t2n else None,
        "by_task_n": {"t1_now": t1n, "t2_team": t2n},
        "tasks": args.tasks,
        "samples": samples,
    }

    gates_hit = {k: (report[k] is not None and report[k] >= v) for k, v in GATES.items()
                 if report.get(k) is not None}
    report["gates"] = gates_hit
    report["pass"] = all(gates_hit.values()) if gates_hit else False

    print(json.dumps(report, ensure_ascii=False, indent=2))
    os.makedirs(args.adapter or ".", exist_ok=True)
    with open(os.path.join(args.adapter, "eval-report.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    return 0 if report["pass"] else 1


if __name__ == "__main__":
    sys.exit(main())
