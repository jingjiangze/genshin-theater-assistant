// 跨平台 CLI 的最小支撑：只含纯逻辑需要的东西（不引 WinForms / OCR）。
// 与 app/ 里的同名类型用途一致，但刻意分开，保证 cli 项目能在 Linux 上构建。
using System.Text.Json;

namespace TheaterAssistant;

internal static class AppPaths
{
    /// <summary>数据目录：优先 THEATER_DATA，其次从当前目录/程序目录向上找含 data/season*.json 的根。</summary>
    public static string Dir { get; private set; } = Directory.GetCurrentDirectory();

    public static string Base => AppContext.BaseDirectory;

    public static void Init(string dataDir = null)
    {
        if (!string.IsNullOrWhiteSpace(dataDir)) { Dir = Path.GetFullPath(dataDir); return; }
        var env = Environment.GetEnvironmentVariable("THEATER_DATA");
        if (!string.IsNullOrWhiteSpace(env)) { Dir = Path.GetFullPath(env); return; }
        var cur = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (cur != null)
        {
            if (Directory.Exists(Path.Combine(cur.FullName, "data"))
                && Directory.GetFiles(Path.Combine(cur.FullName, "data"), "season*.json").Length > 0)
            { Dir = cur.FullName; return; }
            cur = cur.Parent;
        }
        Dir = Directory.GetCurrentDirectory();
    }

    public static string Data(string name) => Path.Combine(Dir, "data", name);
    public static string TemplatesRoot => Path.Combine(Dir, "templates");
    public static string ModelsRoot => Path.Combine(Dir, "models");
}

internal static class Cli
{
    public static void Attach() { }
    public static void Say(string message) => Console.WriteLine(message);
}

internal static class TemplateFactory
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
