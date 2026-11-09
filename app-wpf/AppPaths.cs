using System;
using System.IO;
using System.Reflection;

namespace TheaterAssistant;

/// <summary>
/// 路径解析（WPF 版）。
///
/// 单文件 exe 的 data/templates 有两种可能归宿：
///   ① 被 IncludeAllContentForSelfExtract 打进 bundle → 解包到 <see cref="Base"/>；
///   ② 走 <b>EmbeddedResource</b> → 首次运行时由本类解包到 %LOCALAPPDATA%\TheaterAssistant\data。
///
/// 为什么要有 ②：实测过静态 Content glob 和动态 Target 都不可靠——
///   静态 glob 在项目求值时展开（CI 里 data 是运行时才复制的，匹配不到）；
///   动态 Content 又触发 NETSDK1152 或时机不对（dist-wpf/data 文件数始终为 0）。
///   嵌入资源在**编译期**就确定的，最稳，模型一直用的就是这个方式。
/// </summary>
internal static class AppPaths
{
    private static string _extractRoot;

    /// <summary>exe 所在目录（用户可见：报告、roster.json、uid.txt）。</summary>
    public static string Dir
    {
        get
        {
            var p = Environment.ProcessPath;
            var d = string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p);
            return string.IsNullOrEmpty(d) ? AppContext.BaseDirectory : d;
        }
    }

    /// <summary>单文件解包目录。</summary>
    public static string Base => AppContext.BaseDirectory;

    /// <summary>嵌入资源解包后的根目录。</summary>
    public static string ExtractRoot
    {
        get
        {
            if (_extractRoot != null) return _extractRoot;
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TheaterAssistant", "data");
            try { Directory.CreateDirectory(root); } catch { }
            _extractRoot = root;
            return root;
        }
    }

    /// <summary>依次在「exe 同级 → 解包目录 → 嵌入资源解包」里找相对路径。</summary>
    public static string Resolve(string relative)
    {
        foreach (var root in new[] { Dir, Base })
        {
            var p = Path.Combine(root, relative);
            if (File.Exists(p) || Directory.Exists(p)) return p;
        }

        // 嵌入资源兜底（如 data/guide-corpus.json → 资源名 TheaterAssistant.data.guide-corpus.json）
        if (relative.StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
        {
            var extracted = TryExtract(relative);
            if (extracted != null) return extracted;
        }

        return Path.Combine(Dir, relative);
    }

    public static string Data(string name) => Resolve(Path.Combine("data", name));

    public static string TemplatesRoot => Resolve("templates");

    public static string ModelsRoot => Resolve("models");

    // ── 嵌入资源解包 ────────────────────────────────────────────
    private static string TryExtract(string relative)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // 资源名可能是原样（含 \ 或 /），也可能是转成点的形态，两种都试
            var raw = "TheaterAssistant.res." + relative.Replace('/', '\\');
            var dotted = "TheaterAssistant.res." + relative.Replace('/', '.').Replace('\\', '.');
            var s = asm.GetManifestResourceStream(raw) ?? asm.GetManifestResourceStream(dotted);
            if (s == null) return null;

            var target = Path.Combine(ExtractRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // 已存在且大小一致则跳过（避免每次启动重写）
            if (!File.Exists(target) || new FileInfo(target).Length != s.Length)
            {
                using var fs = File.Create(target);
                s.CopyTo(fs);
            }
            return target;
        }
        catch { return null; }
    }

    /// <summary>诊断用：报告资源里有多少个 data 文件。</summary>
    public static int ResourceDataCount()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            int n = 0;
            foreach (var r in asm.GetManifestResourceNames())
                if (r.StartsWith("TheaterAssistant.res.data.", StringComparison.Ordinal)) n++;
            return n;
        }
        catch { return 0; }
    }
}
