# 剧诗专用小模型（SFT）

目标：给 ◇ 模型层训一个**只会说剧诗话**的小模型，在量化层与语料都拿不准时补位；不联网、离线可用。
选型结论（2026-11-10 实测）：**MiniMind2-Small（26M）全参微调**，fp16 约 52MB，是 0.5B 量化（400MB）的 1/8。

## 数据从哪来

全部来自仓库里已有的"真实结论"，不臆造：

| 任务 | 来源 | 样本 |
|---|---|---|
| T1 现在做 | `data/act-playbook.json` + `data/rotations.json`（每个 幕×难度） | 160 |
| T2 配队 | `data/act-teams.json`（54 份实战字幕的出场率） | 18 |
| T3 攻略问答 | `data/guide-corpus.json`（抽样封顶，防止退化成百科模型） | 240 |

```bash
node tools/ml/build-sft.mjs          # → data/ml/theater-sft-{train,val}.jsonl + manifest.json
```
确定性 seed（`--seed 20261110`），同一份数据永远产出同一份切分；构建时自校验（空内容/重复/长度）。

## 训练（本机，Intel 显卡）

```bat
:: 1) 环境（一次性；国内用 aliyun 源——官方 XPU 轮子会下载停滞、tsinghua 对大轮子 403）
python -m venv .mlvenv
.mlvenv\Scripts\python.exe -m pip install -i https://mirrors.aliyun.com/pypi/simple torch transformers peft

:: 2) 国内拉模型
set HF_ENDPOINT=https://hf-mirror.com

:: 3) 冒烟（只跑 20 步，验证设备/依赖/数据链路）
.mlvenv\Scripts\python.exe tools\ml\train_lora.py --model jingyaogong/MiniMind2-Small ^
    --data data/ml --out out/smoke --full --smoke

:: 4) 正式：小模型全参微调（推荐）
.mlvenv\Scripts\python.exe tools\ml\train_lora.py --model jingyaogong/MiniMind2-Small ^
    --data data/ml --out out/minimind-26m-full --epochs 8 --full --lr 5e-5 --max-len 320 --grad-accum 16
```

**为什么是 MiniMind2-Small + 全参**（两次实测的结论）：

| 方案 | 量化后体积 | 实测 |
|---|---|---|
| Qwen2.5-0.5B-Instruct + LoRA | Q4 ≈ 400MB | 用户判定太大，弃用（val_loss 2.97，1 epoch 24 分钟） |
| MiniMind2-Small + LoRA(r=8) | fp16 ≈ 52MB | **欠拟合**：loss 卡在 2.7，生成退化成重复串 |
| **MiniMind2-Small 全参（26M 全部参数）** | fp16 ≈ 52MB | loss 4.1 → 1.0，33 步/3 轮仅 12 分钟 |

MiniMind2-Small 是标准 `LlamaForCausalLM`（config.json 无自定义代码）+ Qwen 式 chat template，
训练/评测脚本**原样可用**，不需要 trust_remote_code 的远端实现。

设备：`--device auto`（默认）= `xpu`（Intel Arc，装 `torch --index-url https://download.pytorch.org/whl/xpu`）> `cuda` > `cpu`。
换好 XPU 环境后加 `--device xpu` 即可，脚本无需改。

## 评测（决策效果门槛）

```bat
.mlvenv\Scripts\python.exe tools\ml\eval.py --base jingyaogong/MiniMind2-Small ^
    --adapter out/minimind-26m-full --data data/ml
```

门槛只打在**决策任务**上（t1 现在做 / t2 配队）——生产架构里 t3 语料问答走 ○语料检索层（`GuideIndex`），
模型只在量化层与语料都拿不准时补位，所以 t3 不参与门槛（`--tasks all` 可全量查看）：

| 指标 | 含义 | 门槛 |
|---|---|---|
| `action_rate` | 回答含「现在做/优先/选第/依据」 | ≥0.8 |
| `t1_exact` | 「现在做」与 playbook 金标**精确匹配**（空白归一） | ≥0.7 |
| `act_match_rate` | 提到幕次时与金标一致 | ≥0.8 |
| `length_ok_rate` | ≤120 字（超了会挡视野） | ≥0.9 |

评测不过线就不准上线——这是「保证决策效果」的机制：**门槛是硬门，不是参考值**。

## 不进 CI

GitHub runner 无 GPU 且训练不可复现；CI 只跑 `build-sft.mjs` 的数据校验（秒级）。
权重在 `out/`（gitignore），不提交仓库。

## 接入客户端（PENDING）

训练产物是全参权重（fp16 约 52MB）。要进产品需要：
1. 转 GGUF（上游 `MiniMind2-gguf` 有现成配方）→ llama.cpp，或转 ONNX（OpenVINO 吃 Intel GPU）；
2. 客户端在 `data/llm.json` 里把端点指向本地推理进程（`LlmAdvisor` 走 HTTP，最小改动）。

这一步未做。
