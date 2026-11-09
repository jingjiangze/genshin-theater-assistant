"""剧诗专用小模型 LoRA 微调（Intel Arc / CUDA / CPU 自适应）。

数据由 tools/ml/build-sft.mjs 生成（messages 格式）：
    data/ml/theater-sft-train.jsonl
    data/ml/theater-sft-val.jsonl

用法（Windows 本机，Intel 显卡）：
    .mlvenv/Scripts/python.exe tools/ml/train_lora.py --model Qwen/Qwen2.5-0.5B-Instruct \\
        --data data/ml --out out/lora-qwen05-theater --epochs 2 --smoke

    --smoke        只跑 20 步，用来验证「设备/依赖/数据」这条链路，不出可用模型
    --device xpu    强制设备（默认 auto：xpu > cuda > mps > cpu）

为什么默认 Qwen2.5-0.5B：本项目交付形态是「单文件 exe + 离线」，0.5B 量化后 ~400MB 进得来；
更大的模型在 Intel 核显/独显上推理延迟会压不住 1s 的刷新节拍。
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from dataclasses import dataclass
from typing import Any

import torch
from datasets import load_dataset
from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training
from transformers import (
    AutoModelForCausalLM,
    AutoTokenizer,
    DataCollatorForSeq2Seq,
    Trainer,
    TrainingArguments,
)


def pick_device(forced: str = "auto") -> str:
    if forced != "auto":
        return forced
    try:
        if torch.xpu.is_available():          # Intel XPU（torch 2.5+ 自带）
            return "xpu"
    except Exception:
        pass
    if torch.cuda.is_available():
        return "cuda"
    if getattr(torch.backends, "mps", None) and torch.backends.mps.is_available():
        return "mps"
    return "cpu"


def dtype_for(device: str) -> torch.dtype:
    if device == "xpu":
        try:
            return torch.bfloat16            # Arc B 系列支持 bf16
        except Exception:
            return torch.float16
    if device in ("cuda", "mps"):
        return torch.float16
    return torch.float32                     # CPU 训练必须 fp32


def load_jsonl(path: str):
    rows = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return rows


def build_encoded(rows, tokenizer, max_len: int):
    """把 messages 编成 input_ids/labels；prompt 部分的 label 置 -100（只学回答）。"""
    out = []
    for r in rows:
        msgs = r["messages"]
        prompt_ids = tokenizer.apply_chat_template(
            msgs[:-1], tokenize=True, add_generation_prompt=True)
        full_ids = tokenizer.apply_chat_template(
            msgs, tokenize=True, add_generation_prompt=False)

        if len(full_ids) > max_len:
            full_ids = full_ids[:max_len]
        prompt_len = min(len(prompt_ids), len(full_ids))
        labels = list(full_ids)
        for i in range(prompt_len):
            labels[i] = -100

        out.append({
            "input_ids": full_ids,
            "attention_mask": [1] * len(full_ids),
            "labels": labels,
        })
    return out


@dataclass
class TokDataset(torch.utils.data.Dataset):
    rows: list

    def __len__(self):
        return len(self.rows)

    def __getitem__(self, i):
        return self.rows[i]


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
    ap.add_argument("--merge", action="store_true", help="训练后合并权重（导出用）")
    args = ap.parse_args()

    device = pick_device(args.device)
    dtype = dtype_for(device)
    print(f"[env] torch={torch.__version__} device={device} dtype={dtype}")
    if device == "xpu":
        print(f"[env] xpu={torch.xpu.get_device_name(0)}")

    tokenizer = AutoTokenizer.from_pretrained(args.model, trust_remote_code=True)
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    model = AutoModelForCausalLM.from_pretrained(
        args.model, torch_dtype=dtype, trust_remote_code=True)
    model.config.use_cache = False
    model.gradient_checkpointing_enable()
    model = prepare_model_for_kbit_training(model) if False else model

    lora = LoraConfig(
        r=16, lora_alpha=32, lora_dropout=0.05, bias="none", task_type="CAUSAL_LM",
        target_modules=["q_proj", "k_proj", "v_proj", "o_proj",
                        "gate_proj", "up_proj", "down_proj"],
    )
    model = get_peft_model(model, lora)
    model.print_trainable_parameters()

    train_rows = build_encoded(load_jsonl(os.path.join(args.data, "theater-sft-train.jsonl")),
                               tokenizer, args.max_len)
    val_rows = build_encoded(load_jsonl(os.path.join(args.data, "theater-sft-val.jsonl")),
                             tokenizer, args.max_len)
    print(f"[data] train={len(train_rows)} val={len(val_rows)}")

    collator = DataCollatorForSeq2Seq(tokenizer, padding=True, label_pad_token_id=-100)

    targs = TrainingArguments(
        output_dir=args.out,
        per_device_train_batch_size=args.batch,
        per_device_eval_batch_size=args.batch,
        gradient_accumulation_steps=args.grad_accum,
        num_train_epochs=args.epochs,
        max_steps=20 if args.smoke else -1,
        learning_rate=args.lr,
        lr_scheduler_type="cosine",
        warmup_ratio=0.03,
        logging_steps=5,
        save_strategy="no",
        eval_strategy="steps" if val_rows else "no",
        eval_steps=50,
        report_to=[],
        bf16=(dtype == torch.bfloat16),
        fp16=(dtype == torch.float16 and device != "cpu"),
        use_cpu=(device == "cpu"),
        dataloader_pin_memory=False,
        remove_unused_columns=False,
    )

    trainer = Trainer(
        model=model,
        args=targs,
        train_dataset=TokDataset(train_rows),
        eval_dataset=TokDataset(val_rows) if val_rows else None,
        data_collator=collator,
    )

    trainer.train()
    os.makedirs(args.out, exist_ok=True)
    model.save_pretrained(args.out)
    tokenizer.save_pretrained(args.out)
    print(f"[done] adapter → {args.out}")

    if args.merge:
        merged = model.merge_and_unload()
        merged_dir = args.out + "-merged"
        merged.save_pretrained(merged_dir)
        tokenizer.save_pretrained(merged_dir)
        print(f"[done] merged → {merged_dir}")

    # 训练摘要（供 CI/日志核对，不含用户数据）
    summary = {
        "model": args.model,
        "device": device,
        "dtype": str(dtype),
        "train": len(train_rows),
        "val": len(val_rows),
        "epochs": args.epochs,
        "smoke": args.smoke,
        "steps": trainer.state.global_step,
        "final_loss": (trainer.state.log_history[-1].get("loss")
                       if trainer.state.log_history else None),
    }
    with open(os.path.join(args.out, "train-summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)
    print("[summary] " + json.dumps(summary, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
