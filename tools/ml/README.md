# 剧诗专用小模型（SFT）

目标：给 ◇ 模型层训一个**只会说剧诗话**的小模型（默认 Qwen2.5-0.5B-Instruct + LoRA），
在量化层与语料都拿不准时补位；不联网、离线可用。

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
:: 1) 环境（一次性）
python -m venv .mlvenv
.mlvenv\Scripts\python.exe -m pip install -i https://mirrors.aliyun.com/pypi/simple torch transformers peft

:: 2) 国内拉模型
set HF_ENDPOINT=https://hf-mirror.com

:: 3) 冒烟（验证设备/依赖/数据链路，只跑 20 步）
.mlvenv\Scripts\python.exe tools\ml\train_lora.py --model Qwen/Qwen2.5-0.5B-Instruct ^
    --data data/ml --out out/smoke --smoke

:: 4) 正式跑
.mlvenv\Scripts\python.exe tools\ml\train_lora.py --model Qwen/Qwen2.5-0.5B-Instruct ^
    --data data/ml --out out/lora-qwen05-theater --epochs 2
```

**设备选择**：`--device auto`（默认）= `xpu`（Intel Arc，装 `torch --index-url https://download.pytorch.org/whl/xpu`）> `cuda` > `cpu`。
本机实测：XPU 轮子 781MB 在国内网络下会停在下不动，退用 aliyun 的 CPU 轮（`2.15.0+cpu`）也能跑通；
换好 XPU 环境后加 `--device xpu` 即可，脚本无需改。

## 评测

```bat
.mlvenv\Scripts\python.exe tools\ml\eval.py --base Qwen/Qwen2.5-0.5B-Instruct ^
    --adapter out/lora-qwen05-theater --data data/ml
```

四项指标（都可在留出集上复算）：

| 指标 | 含义 | 底线 |
|---|---|---|
| `action_rate` | 回答里含「现在做/优先/选第/依据」 | ≥0.8 |
| `act_match_rate` | 提到幕次时与金标一致（金标没提幕则算对） | ≥0.8 |
| `length_ok_rate` | ≤120 字（超了戴在屏幕上会挡视野） | ≥0.9 |
| `entity_recall` | 金标里的角色名/数字覆盖多少 | 参考值，不设门槛 |

## 不进 CI

GitHub runner 无 GPU 且训练不可复现；CI 只跑 `build-sft.mjs` 的数据校验（秒级）。
权重在 `out/`（gitignore），不提交仓库。

## 接入客户端（PENDING）

训练产物是 LoRA adapter。要进产品需要一步导出：
1. `--merge` 合并权重；
2. 转 ONNX（OpenVINO 可吃）或 GGUF（llama.cpp）；
3. 客户端在 `data/llm.json` 里把端点指向本地推理进程。

当前 `LlmAdvisor` 走的是 HTTP 端点，接本地进程是最小改动路径；这一步未做。
