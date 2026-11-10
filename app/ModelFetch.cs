// ─────────────────────────────────────────────────────────────────────────────
// 小模型可选下载（瘦包分发用）：默认随包内嵌 GGUF；若要发不带模型的瘦包，
// 首次运行/手动执行 `--fetch-model <url>` 即可把 GGUF 落到 models\llm\。
//
// 设计要点：
//   · 只接受 https，且校验大小/可选 sha256（防止下到错误文件后静默用错模型）；
//   · 先写 .part 再原子改名——半截文件永远不会被当成可用模型；
//   · 已存在且大小一致则跳过（幂等）。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace TheaterAssistant;

internal static class ModelFetch
{
    public const string FileName = "minimind-26m-q8.gguf";

    /// <summary>模型落盘位置（models\llm\，与内嵌解包同路径）。</summary>
    public static string TargetPath => Path.Combine(AppPaths.ModelsRoot, "llm", FileName);

    public static int RunCli(string url, string sha256 = null, long expectedBytes = 0)
    {
        Cli.Attach();

        // 没给 URL 时尝试从 data/llm.json 的 embeddedModelUrl 读
        if (string.IsNullOrWhiteSpace(url))
        {
            url = ReadUrlFromConfig();
            if (string.IsNullOrWhiteSpace(url))
            {
                Cli.Say("缺 URL：--fetch-model <https://...> 或在 data/llm.json 写 embeddedModelUrl");
                return 2;
            }
        }

        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Cli.Say("只接受 https URL（模型文件不该走明文）");
            return 2;
        }

        var target = TargetPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            Cli.Say("下载: " + url);
            using var resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) { Cli.Say($"HTTP {(int)resp.StatusCode}"); return 3; }

            var total = resp.Content.Headers.ContentLength ?? expectedBytes;
            var part = target + ".part";
            using (var input = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
            using (var output = File.Create(part))
            {
                input.CopyTo(output);
            }

            var len = new FileInfo(part).Length;
            if (total > 0 && len != total)
            {
                File.Delete(part);
                Cli.Say($"大小不符：拿到 {len}，期望 {total}");
                return 4;
            }

            if (!string.IsNullOrWhiteSpace(sha256))
            {
                using var fs = File.OpenRead(part);
                var got = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                if (!string.Equals(got, sha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                {
                    File.Delete(part);
                    Cli.Say($"sha256 不符：拿到 {got}，期望 {sha256}");
                    return 5;
                }
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
            Cli.Say($"完成: {target}（{len / 1048576.0:0.0}MB）");
            return 0;
        }
        catch (Exception ex)
        {
            Cli.Say("下载失败: " + ex.GetType().Name + ": " + ex.Message);
            return 6;
        }
    }

    private static string ReadUrlFromConfig()
    {
        try
        {
            var path = AppPaths.Data("llm.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("embeddedModelUrl", out var u) ? u.GetString() : null;
        }
        catch { return null; }
    }
}
