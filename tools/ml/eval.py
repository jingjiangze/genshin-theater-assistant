"""剧诗小模型评测：在留出集上跑生成，算四个可证伪的指标。

不追求"好看的分"，只回答四个问题：
  1) 是否给出了可照做的一句（含「现在做/优先/选第/依据」之一）——空的/闲聊的直接判负；
  2) 幕次是否对得上（回答里出现的「第N幕」与样本一致，或样本本身没提幕）；
  3) 角色/数字这类硬实体召回（金标里的中文名/数字，回答覆盖多少）；
  4) 长度是否在可播报范围（≤120 字，超了戴在屏幕上会挡视野）。

用法：
    .mlvenv/Scripts/python.exe tools/ml/eval.py --base Qwen/Qwen2.5-0.5B-Instruct \\
        --adapter out/lora-qwen05-theater --data data/ml --limit 42
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


def load_jsonl(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(l) for l in f if l.strip()]


def entities(text: str) -> set[str]:
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
    ap.add_argument("--max-new", type=int, default=160)
    ap.add_argument("--device", default="auto")
    args = ap.parse_args()

    device = args.device
    if device == "auto":
        device = "xpu" if getattr(torch, "xpu", None) and torch.xpu.is_available() else (
            "cuda" if torch.cuda.is_available() else "cpu")
    dtype = torch.bfloat16 if device == "xpu" else (torch.float16 if device == "cuda" else torch.float32)

    tok = AutoTokenizer.from_pretrained(args.base, trust_remote_code=True)
    model = AutoModelForCausalLM.from_pretrained(args.base, torch_dtype=dtype, trust_remote_code=True)
    if os.path.isdir(args.adapter):
        model = PeftModel.from_pretrained(model, args.adapter)
    model.to(device).eval()

    rows = load_jsonl(os.path.join(args.data, "theater-sft-val.jsonl"))
    if args.limit:
        rows = rows[: args.limit]

    n = ok_action = ok_act = ok_len = 0
    ent_hit = ent_all = 0
    samples = []

    for r in rows:
        msgs = r["messages"]
        prompt = tok.apply_chat_template(msgs[:-1], tokenize=False, add_generation_prompt=True)
        ids = tok(prompt, return_tensors="pt").to(device)
        with torch.no_grad():
            out = model.generate(**ids, max_new_tokens=args.max_new, do_sample=False,
                                 pad_token_id=tok.eos_token_id)
        text = tok.decode(out[0][ids["input_ids"].shape[1]:], skip_special_tokens=True).strip()
        gold = msgs[-1]["content"]

        n += 1
        if any(h in text for h in ACTION_HINTS):
            ok_action += 1
        gold_act = ACT_RE.search(gold)
        if not gold_act or ACT_RE.search(text) and ACT_RE.search(text).group(1) == gold_act.group(1):
            ok_act += 1
        if 0 < len(text) <= 120:
            ok_len += 1
        ge, te = entities(gold), entities(text)
        if ge:
            ent_all += len(ge)
            ent_hit += len(ge & te)

        if len(samples) < 5:
            samples.append({"user": msgs[1]["content"], "gold": gold[:200], "pred": text[:200]})

    report = {
        "n": n,
        "action_rate": round(ok_action / n, 3) if n else 0,
        "act_match_rate": round(ok_act / n, 3) if n else 0,
        "length_ok_rate": round(ok_len / n, 3) if n else 0,
        "entity_recall": round(ent_hit / ent_all, 3) if ent_all else 0,
        "device": device,
        "adapter": args.adapter,
        "samples": samples,
    }
    print(json.dumps(report, ensure_ascii=False, indent=2))
    os.makedirs(os.path.dirname(args.adapter) or ".", exist_ok=True)
    with open(os.path.join(args.adapter, "eval-report.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
    return 0


if __name__ == "__main__":
    sys.exit(main())
