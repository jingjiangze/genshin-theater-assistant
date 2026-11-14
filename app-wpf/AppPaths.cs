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
            // 注意：Resolve 传入的 relative 已含 "data/" 前缀，
            // ExtractRoot 若再带 data 会解到 data/data/（实测踩过）。
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TheaterAssistant");
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

        // 嵌入资源兜底（如 data/guide-corpus.json → 资源名 TheaterAssistant.res.data.guide-corpus.json）
        // models/ 同理：内嵌小模型 GGUF 会被解到 %LOCALAPPDATA%\TheaterAssistant\models\llm\
        if (relative.StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("models" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
        {
            var extracted = TryExtract(relative);
            if (extracted != null) return extracted;
        }

        return Path.Combine(Dir, relative);
    }

    public static string Data(string name) => Resolve(Path.Combine("data", name));

    /// <summary>
    /// 模板根目录。**必须能解包**：WPF 版把 templates/ 打成 EmbeddedResource，
    /// 而 Resolve 的嵌入兜底只覆盖 data//models/ —— 早先这里直接返回 exe 同级 templates/（不存在），
    /// 结果生产里模板数恒为 0、模板匹配整层静默失效（2026-11-14 用忠实预览发现）。
    /// </summary>
    public static string TemplatesRoot
    {
        get
        {
            var p = Resolve("templates");
            if (Directory.Exists(p) && Directory.GetFiles(p, "*.png", SearchOption.AllDirectories).Length > 0)
                return p;
            return ExtractTree("templates") ?? p;
        }
    }

    public static string ModelsRoot => Resolve("models");

    /// <summary>内嵌小模型的 GGUF 路径（EmbeddedResource 解包 → 或 exe 同级 models/llm/）。</summary>
    public static string LocalModelPath => Resolve(Path.Combine("models", "llm", "minimind-26m-q8.gguf"));

    /// <summary>
    /// 把 `TheaterAssistant.res.&lt;prefix&gt;.*` 这类**目录树**资源整体解包到
    /// %LOCALAPPDATA%\TheaterAssistant\&lt;prefix&gt;\（保留子目录）。
    /// 名字里的分隔符是反斜杠（MSBuild 的 %(RecursiveDir)），这里按原样还原成目录。
    /// </summary>
    private static string? ExtractTree(string prefix)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var head = "TheaterAssistant.res." + prefix + ".";
            var wrote = 0;
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith(head, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = name[head.Length..].Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                if (string.IsNullOrEmpty(rel) || !Path.HasExtension(rel)) continue;   // 跳过目录占位
                var target = Path.Combine(ExtractRoot, prefix, rel);
                try { Directory.CreateDirectory(Path.GetDirectoryName(target)!); } catch { }
                using var src = asm.GetManifestResourceStream(name);
                if (src == null) continue;
                if (!File.Exists(target) || new FileInfo(target).Length != src.Length)
                {
                    using var fs = File.Create(target);
                    src.CopyTo(fs);
                    wrote++;
                }
            }
            var root = Path.Combine(ExtractRoot, prefix);
            if (Directory.Exists(root) && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length > 0)
                return root;
            return null;
        }
        catch { return null; }
    }

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

            // 兜底：按文件名后缀扫描（%(RecursiveDir) 拼出来的名字形态难预测，实测踩过）
            if (s == null)
            {
                var leaf = Path.GetFileName(relative);
                foreach (var name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith("." + leaf, StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(leaf, StringComparison.OrdinalIgnoreCase))
                    {
                        s = asm.GetManifestResourceStream(name);
                        if (s != null) break;
                    }
                }
            }
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
