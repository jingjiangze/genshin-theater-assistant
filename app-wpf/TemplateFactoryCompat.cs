// WPF 版的编译兼容层。
//
// 背景：`app/QuantScorer.cs` 里用到了 `TemplateFactory.JsonOpts`（仅一个 JSON 序列化配置），
// 但真正的 `TemplateFactory` 在 app/TemplateFactory.cs，它依赖 System.Drawing（模板派生要用 Bitmap）。
// WPF 项目不应该为了一个序列化选项把整个 GDI+ 依赖拖进来，所以这里只提供**同名的静态配置**。
// 两个项目各自独立编译，不会冲突。
using System.Text.Json;

namespace TheaterAssistant;

internal static class TemplateFactory
{
    /// <summary>与 WinForms 版保持一致的 JSON 序列化选项（缩进、不转义中文）。</summary>
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
