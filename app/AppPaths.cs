// ─────────────────────────────────────────────────────────────────────────────
// 从 app/OverlayForm.cs 抽出：WPF 版（app-wpf）与 WinForms 版（app）共用同一份路径解析。
// 抽出的原因：WPF 项目不引用 OverlayForm.cs（那是 WinForms 窗体），但同样需要 AppPaths。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.IO;

namespace TheaterAssistant;

/// <summary>
/// 单文件 exe 若开启 IncludeAllContentForSelfExtract，AppPaths.Dir 可能指向解包临时目录；
/// 用户自己放在 exe 旁边的 templates/data 就找不到。统一以 exe 所在目录为准。
/// </summary>
internal static class AppPaths
{
    /// <summary>exe 所在目录（用户可见的落盘位置：报告、素材、roster.json）。</summary>
    public static string Dir
    {
        get
        {
            var p = Environment.ProcessPath;
            var d = string.IsNullOrEmpty(p) ? null : Path.GetDirectoryName(p);
            return string.IsNullOrEmpty(d) ? AppContext.BaseDirectory : d;
        }
    }

    /// <summary>单文件 exe 的解包目录：data/templates/models 被 IncludeAllContentForSelfExtract 解到这里。</summary>
    public static string Base => AppContext.BaseDirectory;

    /// <summary>
    /// 依次在「exe 同级 → 解包目录」里找相对路径，返回第一个存在的。
    /// 这是必须的：发布包把 data/、templates/ 打进单文件 exe，运行期只在解包目录里存在；
    /// 而用户自己往 exe 旁边放的同名目录要优先（覆盖内置）。都没有就返回 exe 同级，便于报错定位。
    /// </summary>
    public static string Resolve(string relative)
    {
        foreach (var root in new[] { Dir, Base })
        {
            var p = Path.Combine(root, relative);
            if (File.Exists(p) || Directory.Exists(p)) return p;
        }
        return Path.Combine(Dir, relative);
    }

    public static string Data(string name) => Resolve(Path.Combine("data", name));
    public static string TemplatesRoot => Resolve("templates");
    public static string ModelsRoot => Resolve("models");
}

