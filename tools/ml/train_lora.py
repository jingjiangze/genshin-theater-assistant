"""剧诗专用小模型 LoRA 微调（Intel XPU / CUDA / CPU 自适应，自带训练循环）。

为什么不用 transformers.Trainer：2026-11 的 transformers 5.x 里 DataCollator 返回
BatchEncoding、Trainer 又不接受它，参数名也改过（warmup_ratio 被移除）。
这套训练只需要「前向 → 反向 → 累积 → 步进」，自带循环反而更稳。

数据（tools/ml/build-sft.mjs 生成，messages 格式）：
    data/ml/theater-sft-train.jsonl / theater-sft-val.jsonl

用法（Windows 本机，Intel 显卡）：
    set HF_ENDPOINT=https://hf-mirror.com
    .mlvenv/Scripts/python.exe tools/ml/train_lora.py --model Qwen/Qwen2.5-0.5B-Instruct \
        --data data/ml --out out/lora-qwen05-theater --epochs 2

    --smoke        只跑 20 步（验证设备/依赖/数据链路）
    --device xpu   强制设备；默认 auto = xpu > cuda > cpu
"""
from __future__ import annotations

import argparse
import json
import math
import os
import sys
import time

import torch
from peft import LoraConfig, get_peft_model
from transformers import AutoModelForCausalLM, AutoTokenizer


def pick_device(forced: str = "auto") -> str:
    if forced != "auto":
        return forced
    try:
        if torch.xpu.is_available():
            return "xpu"
    except Exception:
        pass
    if torch.cuda.is_available():
        return "cuda"
    return "cpu"


def dtype_for(device: str) -> torch.dtype:
    return torch.bfloat16 if device in ("xpu", "cuda") else torch.float32


def load_jsonl(path: str):
    rows = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return rows


def encode(rows, tokenizer, max_len: int):
    """messages → input_ids/labels；prompt 段 label 置 -100（只学回答部分）。"""
    out = []
    for r in rows:
        msgs = r["messages"]
        # 先出文本再显式 tokenize：transformers 5.x 的 apply_chat_template(tokenize=True)
        # 返回 BatchEncoding，直接当 list 用会得到字符串键（实测报 "too many dimensions 'str'"）。
        prompt_text = tokenizer.apply_chat_template(msgs[:-1], tokenize=False, add_generation_prompt=True)
        full_text = tokenizer.apply_chat_template(msgs, tokenize=False, add_generation_prompt=False)
        prompt_ids = tokenizer(prompt_text, add_special_tokens=False)["input_ids"]
        full_ids = tokenizer(full_text, add_special_tokens=False)["input_ids"]
        if len(full_ids) > max_len:
            full_ids = full_ids[:max_len]
        prompt_len = min(len(prompt_ids), len(full_ids))
        labels = list(full_ids)
        for i in range(prompt_len):
            labels[i] = -100
        out.append({"input_ids": full_ids, "labels": labels})
    return out


class TokDataset(torch.utils.data.Dataset):
    def __init__(self, rows):
        self.rows = rows

    def __len__(self):
        return len(self.rows)

    def __getitem__(self, i):
        return self.rows[i]


class PadCollator:
    """自己填 padding（不依赖 transformers 的 collator 实现）。"""

    def __init__(self, pad_id: int):
        self.pad_id = pad_id

    def __call__(self, features):
        maxlen = max(len(f["input_ids"]) for f in features)
        ids, mask, labels = [], [], []
        for f in features:
            pad = maxlen - len(f["input_ids"])
            ids.append(list(f["input_ids"]) + [self.pad_id] * pad)
            mask.append([1] * len(f["input_ids"]) + [0] * pad)
            labels.append(list(f["labels"]) + [-100] * pad)
        return {
            "input_ids": torch.tensor(ids, dtype=torch.long),
            "attention_mask": torch.tensor(mask, dtype=torch.long),
            "labels": torch.tensor(labels, dtype=torch.long),
        }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="Qwen/Qwen2.5-0.5B-Instruct")
    ap.add_argument("--data", default="data/ml")
    ap.add_argument("--out", default="out/lora-theater")
    ap.add_argument("--epochs", type=float, default=2.0)
    ap.add_argument("--max-len", type=int, default=512)
    ap.add_argument("--batch", type=int, default=2)
    ap.add_argument("--grad-accum", type=int, default=8)
    ap.add_argument("--lr", type=float, default=2e-4)
    ap.add_argument("--device", default="auto")
    ap.add_argument("--smoke", action="store_true", help="只跑 20 步验证链路")
    args = ap.parse_args()

    device = pick_device(args.device)
    dtype = dtype_for(device)
    print(f"[env] torch={torch.__version__} device={device} dtype={dtype}", flush=True)
    if device == "xpu":
        print(f"[env] xpu={torch.xpu.get_device_name(0)}", flush=True)

    tokenizer = AutoTokenizer.from_pretrained(args.model, trust_remote_code=True)
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    model = AutoModelForCausalLM.from_pretrained(args.model, dtype=dtype, trust_remote_code=True)
    model.config.use_cache = False
    model.gradient_checkpointing_enable()
    model.enable_input_require_grads()

    model = get_peft_model(model, LoraConfig(
        r=16, lora_alpha=32, lora_dropout=0.05, bias="none", task_type="CAUSAL_LM",
        target_modules=["q_proj", "k_proj", "v_proj", "o_proj",
                        "gate_proj", "up_proj", "down_proj"]))
    model.print_trainable_parameters()
    model.to(device)

    train_rows = encode(load_jsonl(os.path.join(args.data, "theater-sft-train.jsonl")), tokenizer, args.max_len)
    val_rows = encode(load_jsonl(os.path.join(args.data, "theater-sft-val.jsonl")), tokenizer, args.max_len)
    print(f"[data] train={len(train_rows)} val={len(val_rows)}", flush=True)

    collator = PadCollator(tokenizer.pad_token_id)
    dl = torch.utils.data.DataLoader(TokDataset(train_rows), batch_size=args.batch,
                                     shuffle=True, collate_fn=collator)
    vdl = torch.utils.data.DataLoader(TokDataset(val_rows), batch_size=args.batch,
                                      shuffle=False, collate_fn=collator)

    steps_per_epoch = max(1, len(dl) // args.grad_accum)
    total_steps = 20 if args.smoke else max(1, int(steps_per_epoch * args.epochs))
    warmup = max(1, int(total_steps * 0.03))

    params = [p for p in model.parameters() if p.requires_grad]
    opt = torch.optim.AdamW(params, lr=args.lr)
    sched = torch.optim.lr_scheduler.LambdaLR(
        opt, lambda s: min(1.0, (s + 1) / warmup) *
        (0.5 * (1 + math.cos(math.pi * min(1.0, max(0.0, (s - warmup) / max(1, total_steps - warmup)))))))

    model.train()
    t0, step, micro = time.time(), 0, 0
    running, run_n = 0.0, 0

    stop = False
    while not stop:
        for batch in dl:
            out = model(**{k: v.to(device) for k, v in batch.items()})
            (out.loss / args.grad_accum).backward()
            running += float(out.loss)
            run_n += 1
            micro += 1

            if micro % args.grad_accum == 0:
                torch.nn.utils.clip_grad_norm_(params, 1.0)
                opt.step()
                sched.step()
                opt.zero_grad(set_to_none=True)
                step += 1
                if step % 5 == 0 or step == 1:
                    print(f"[step {step}/{total_steps}] loss={running / max(1, run_n):.4f} "
                          f"lr={sched.get_last_lr()[0]:.2e} {time.time() - t0:.0f}s", flush=True)
                running, run_n = 0.0, 0
                if step >= total_steps:
                    stop = True
                    break
        if args.smoke and step >= total_steps:
            break

    val_loss = None
    if val_rows:
        model.eval()
        tot, n = 0.0, 0
        with torch.no_grad():
            for batch in vdl:
                tot += float(model(**{k: v.to(device) for k, v in batch.items()}).loss)
                n += 1
        val_loss = round(tot / max(1, n), 4)
        print(f"[eval] val_loss={val_loss}", flush=True)

    os.makedirs(args.out, exist_ok=True)
    model.save_pretrained(args.out)
    tokenizer.save_pretrained(args.out)

    summary = {
        "model": args.model, "device": device, "torch": torch.__version__,
        "dtype": str(dtype), "train": len(train_rows), "val": len(val_rows),
        "steps": step, "epochs": args.epochs, "smoke": args.smoke,
        "val_loss": val_loss, "seconds": round(time.time() - t0, 1),
    }
    with open(os.path.join(args.out, "train-summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)
    print("[summary] " + json.dumps(summary, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
