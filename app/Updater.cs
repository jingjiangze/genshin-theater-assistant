using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TheaterAssistant;

/// <summary>
/// 资源包 / 软件更新（MAA 式：程序与资源都可更新，资源走镜像源 + SHA256 校验）。
///
///   --check-update                 查 GitHub Releases 最新 tag 与本机版本
///   --packs                        列出已安装资源包 + 清单里可装的
///   --fetch-model [名字]           下载清单里的资源包到 %LOCALAPPDATA%\TheaterAssistant\packs
///
/// 资源清单：data/models.manifest.json
///   { "packs": [ { "name":"...", "kind":"embedding", "size":12345, "sha256":"...",
///                  "urls":["https://ghproxy/…","https://github.com/…"] } ] }
/// 设计约束：
///  · 只下到 LOCALAPPDATA，不动 exe 所在目录，失败保留旧版本；
///  · 先下到 .part，校验 SHA256 通过才原子替换；
///  · 镜像按顺序试（GitHub 镜像/代理优先，官方最后兜底）。
/// </summary>
internal static class Updater
{
    private const string Repo = "jingjiangze/genshin-theater-assistant";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string PacksRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheaterAssistant", "packs");

    internal sealed class Pack
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
        public string[] Urls { get; set; } = Array.Empty<string>();
        public string Note { get; set; } = "";
    }

    internal sealed class Manifest
    {
        public string Note { get; set; } = "";
        public Pack[] Packs { get; set; } = Array.Empty<Pack>();
    }

    public static Manifest LoadManifest(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Manifest();
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true });
            return m ?? new Manifest();
        }
        catch { return new Manifest(); }
    }

    /// <summary>校验一个文件的 SHA256（大小写不敏感）；空哈希视为"跳过校验"。</summary>
    internal static bool VerifyHash(string file, string expectedSha256)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(expectedSha256)) return true;
            if (!File.Exists(file)) return false;
            using var fs = File.OpenRead(file);
            var hash = SHA256.HashData(fs);
            var hex = Convert.ToHexString(hash);
            return string.Equals(hex, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static int Run(string arg, bool listOnly)
    {
        Cli.Attach();
        var manifest = LoadManifest(AppPaths.Data("models.manifest.json"));

        if (listOnly)
        {
            Cli.Say($"资源包根目录: {PacksRoot}");
            Cli.Say($"清单条目: {manifest.Packs.Length}");
            foreach (var p in manifest.Packs)
            {
                var installed = Directory.Exists(PacksRoot) && Directory.GetFiles(PacksRoot, p.Name + "*").Length > 0;
                Cli.Say($"  {(installed ? "[已装]" : "[未装]")} {p.Name}  kind={p.Kind}  {p.Size / 1024.0 / 1024.0:0.0} MB  sha={Short(p.Sha256)}  {p.Note}");
            }
            if (manifest.Packs.Length == 0)
                Cli.Say("（清单为空：把想内置的资源包写进 data/models.manifest.json，格式见 README「资源包」）");
            return 0;
        }

        var name = arg?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Run(null, true);

        var pack = manifest.Packs.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (pack == null) { Cli.Say("清单里没有: " + name); return 2; }
        if (pack.Urls == null || pack.Urls.Length == 0) { Cli.Say("该资源包没有配置下载地址"); return 2; }

        Directory.CreateDirectory(PacksRoot);
        var dest = Path.Combine(PacksRoot, pack.Name + (pack.Urls[0].EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ? ".onnx" : ".bin"));
        var part = dest + ".part";

        foreach (var url in pack.Urls)
        {
            try
            {
                Cli.Say("尝试下载: " + url);
                using var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) { Cli.Say("  HTTP " + (int)resp.StatusCode); continue; }
                using (var src = resp.Content.ReadAsStream())
                using (var dst = File.Create(part))
                    src.CopyTo(dst);

                if (!VerifyHash(part, pack.Sha256))
                {
                    Cli.Say("  SHA256 不匹配，丢弃");
                    try { File.Delete(part); } catch { }
                    continue;
                }
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(part, dest);
                Cli.Say($"OK → {dest}（{new FileInfo(dest).Length / 1024.0 / 1024.0:0.0} MB，SHA256 校验通过）");
                return 0;
            }
            catch (Exception ex) { Cli.Say("  失败: " + ex.Message); }
        }

        try { if (File.Exists(part)) File.Delete(part); } catch { }
        Cli.Say("所有镜像都失败（保留原有文件，不破坏现有安装）");
        return 1;
    }

    /// <summary>查最新 Release，与本机版本比较（只读、不改任何东西）。</summary>
    public static int CheckUpdate()
    {
        Cli.Attach();
        var current = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);
        Cli.Say($"本机版本: {current}");
        try
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("theater-assistant-updater");
            var json = Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest").GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var published = doc.RootElement.TryGetProperty("published_at", out var p) ? p.GetString() ?? "" : "";
            Cli.Say($"最新发布: {tag}  {name}  ({published})");

            var remote = tag.TrimStart('v', 'V');
            if (Version.TryParse(remote, out var rv))
            {
                if (rv > current) { Cli.Say("有更新 → 打开 Releases 下载 exe 覆盖即可（数据与资源包不用重下）"); return 0; }
                Cli.Say("已是最新");
                return 0;
            }
            Cli.Say("无法解析远端版本号，自行比对即可");
            return 0;
        }
        catch (Exception ex)
        {
            Cli.Say("检查更新失败（网络）: " + ex.Message);
            return 1;
        }
    }

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "-" : sha[..Math.Min(8, sha.Length)];
}
