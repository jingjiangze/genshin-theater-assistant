"""本地推理进程：把剧诗小模型（MiniMind2-Small 26M 全参微调版）挂成 OpenAI 兼容端点。

为什么是"本地进程 + HTTP"而不是改客户端：客户端已有 LlmAdvisor（OpenAI 兼容
chat/completions + 多端点降级 + 脱敏 + 冷却），data/llm.json 指到本进程即可让
◇模型层在环，客户端零改动；进程关掉 = 客户端自动降级回 ○语料/·规则（原有兜底）。

端点：
    POST /v1/chat/completions   {"messages":[...], "max_tokens":96}
                             → {"choices":[{"message":{"role":"assistant","content":"..."}}]}
    GET  /health                → {"ok":true,"backend":"openvino-gpu|torch-cpu","model":"..."}

用法：
    .mlvenv/Scripts/python.exe tools/ml/serve.py --model out/minimind-26m-fp16 --port 8765
    # OpenVINO 导出目录（含 .xml）自动走 Intel GPU：
    .mlvenv/Scripts/python.exe tools/ml/serve.py --model out/minimind-26m-ov --backend auto
"""
from __future__ import annotations

import argparse
import json
import os
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

STATE = {"backend": "", "model": "", "model_obj": None, "tok": None, "max_len": 1024}


def load_torch(path: str):
    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer

    device = "cpu"
    try:
        if torch.xpu.is_available():
            device = "xpu"
    except Exception:
        pass
    if torch.cuda.is_available():
        device = "cuda"
    dtype = torch.float16 if device in ("xpu", "cuda") else torch.float32
    tok = AutoTokenizer.from_pretrained(path, trust_remote_code=True)
    model = AutoModelForCausalLM.from_pretrained(path, dtype=dtype, trust_remote_code=True).to(device).eval()
    STATE["backend"] = f"torch-{device}"

    def generate(ids, max_tokens: int, eos_id: int):
        with torch.no_grad():
            return model.generate(ids, max_new_tokens=max_tokens, do_sample=False, pad_token_id=eos_id)

    return tok, generate


def load_openvino(path: str):
    """OpenVINO 导出目录（optimum-cli export openvino 的产物）→ Intel GPU 优先。"""
    from optimum.intel import OVModelForCausalLM
    from transformers import AutoTokenizer

    gpu = "CPU"
    try:
        import openvino as ov
        devices = ov.Core().available_devices
        gpu = "GPU" if any(d.startswith("GPU") for d in devices) else "CPU"
        print(f"[ov] devices={devices} -> {gpu}", flush=True)
    except Exception as ex:
        print(f"[ov] device probe failed: {ex} -> CPU", flush=True)

    tok = AutoTokenizer.from_pretrained(path, trust_remote_code=True)
    model = OVModelForCausalLM.from_pretrained(path, device=gpu, trust_remote_code=True)
    model.eval()
    STATE["backend"] = f"openvino-{gpu.lower()}"

    def generate(ids, max_tokens: int, eos_id: int):
        return model.generate(ids, max_new_tokens=max_tokens, do_sample=False, pad_token_id=eos_id)

    return tok, generate


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):   # 静默访问日志（调用方有自己的日志/报告）
        pass

    def _json(self, code: int, obj):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/health":
            self._json(200, {"ok": STATE["model_obj"] is not None,
                             "backend": STATE["backend"], "model": STATE["model"]})
        else:
            self._json(404, {"error": "not found"})

    def do_POST(self):
        if self.path != "/v1/chat/completions":
            self._json(404, {"error": "not found"})
            return
        try:
            n = int(self.headers.get("Content-Length", "0"))
            req = json.loads(self.rfile.read(n) or b"{}")
        except Exception as ex:
            self._json(400, {"error": f"bad request: {ex}"})
            return

        messages = req.get("messages") or []
        max_tokens = int(req.get("max_tokens") or 96)
        t0 = time.time()
        try:
            tok = STATE["tok"]
            text = tok.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
            ids = tok(text, return_tensors="pt", add_special_tokens=False,
                      truncation=True, max_length=STATE["max_len"] - max_tokens)["input_ids"]
            out = STATE["model_obj"](ids, max_tokens, tok.eos_token_id)
            content = tok.decode(out[0][ids.shape[1]:], skip_special_tokens=True).strip()
        except Exception as ex:
            self._json(500, {"error": f"generate failed: {type(ex).__name__}: {ex}"})
            return
        self._json(200, {
            "id": "local-minimind",
            "object": "chat.completion",
            "model": STATE["model"],
            "choices": [{"index": 0, "finish_reason": "stop",
                         "message": {"role": "assistant", "content": content}}],
            "usage": {"latency_ms": int((time.time() - t0) * 1000)},
        })


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="out/minimind-26m-fp16")
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--backend", default="auto", choices=["auto", "torch", "ov"])
    ap.add_argument("--fp8", action="store_true",
                    help="fp8 weight-only。仅 GPU 后端有收益；CPU 实测比 fp32 慢 4x（2026-11-10 基准，见 README）")
    ap.add_argument("--int8", action="store_true",
                    help="int8 weight-only。CPU 实测比 fp32 慢 2x；两者都只建议在 OV/XPU 后端开")
    ap.add_argument("--max-len", type=int, default=1024)
    args = ap.parse_args()

    STATE["model"] = args.model
    STATE["max_len"] = args.max_len

    is_ov_dir = os.path.isdir(args.model) and any(f.endswith(".xml") for f in os.listdir(args.model))
    if args.backend in ("ov",) or (args.backend == "auto" and is_ov_dir):
        STATE["tok"], STATE["model_obj"] = load_openvino(args.model)
        if args.fp8 or args.int8:
            # OV 导出可用时此处接 NNCF 权重压缩（fp8/int8）；当前 OV 导出被版本三角卡住，见 README
            print("[warn] OV 后端的 fp8/int8 压缩待 optimum-intel 可用后接入，本次按 fp16 加载", flush=True)
    else:
        if args.fp8 or args.int8:
            # 2026-11-10 基准（本机 CPU，48/64 tok × 3 次取最优）：
            #   fp32 718-919ms｜fp8-wo 2877ms（4x 慢）｜int8-wo 1839ms（2x 慢），输出逐字一致。
            # CPU 没有低精度 matmul，weight-only 是纯解量化开销 → 拒绝并提示。
            raise SystemExit("CPU 后端不支持 --fp8/--int8（实测反而更慢，输出不变）；低精度请在 OV/XPU 后端开")
        STATE["tok"], STATE["model_obj"] = load_torch(args.model)

    print(f"[serve] backend={STATE['backend']} model={args.model} port={args.port}", flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
