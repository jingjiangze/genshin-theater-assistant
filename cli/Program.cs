// 跨平台 CLI 入口：Linux / 云端 box 上跑决策与配队的完整流程。
// 与 Windows 悬浮窗共用同一批纯逻辑源码（见 TheaterCli.csproj 的 Compile Include）。
using System.Text;

namespace TheaterAssistant;

internal static class Program
{
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        if (args.Length == 0) return Help();

        static string Opt(string[] a, string name)
        {
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return null;
        }
        static int Num(string[] a, string name, int fallback) =>
            int.TryParse(Opt(a, name), out var v) ? v : fallback;

        AppPaths.Init(Opt(args, "--data"));
        Cli.Say($"数据目录: {AppPaths.Dir}");

        switch (args[0])
        {
            case "--help" or "-h":
                return Help();

            case "--policy":
                return DecisionPolicy.RunCli();

            case "--seasons":
                return SeasonRegistry.RunCli();

            case "--llm-probe":
                return LlmAdvisor.RunProbe(AppPaths.Data("llm.json"), Array.IndexOf(args, "--dry") >= 0, Array.IndexOf(args, "--verify") >= 0);

            case "--playbook":
                return Playbook.RunCli(Opt(args, "--out"));

            case "--enemy-profile":
                DecisionPolicy.Load(AppPaths.Data("decision-policy.json"));
                Playbook.Load(AppPaths.Data("act-playbook.json"));
                return QuantData.RunEnemyCli(Num(args, "--act", 0), Opt(args, "--difficulty"));

            case "--team-profile":
                DecisionPolicy.Load(AppPaths.Data("decision-policy.json"));
                return QuantData.RunTeamCli();

            case "--quant-decide":
                return QuantScorer.RunCli(Num(args, "--act", 0), Opt(args, "--difficulty"), Opt(args, "--out"));

            case "--team-solve":
                return TeamSolver.RunCli(Opt(args, "--difficulty"), Num(args, "--act", 0),
                    Opt(args, "--out"), Num(args, "--season", 0), Opt(args, "--roster"));

            case "--quality-gates":
                return QualityGates.RunCli(Num(args, "--season", 0));

            case "--global-alloc":
                return GlobalAllocator.RunCli(Num(args, "--season", 0), Opt(args, "--out"), Opt(args, "--roster"));

            case "--orchestrate":
                return DecisionOrchestrator.RunCli(Num(args, "--act", 0), Opt(args, "--difficulty"));

            case "--ocr":
                return OcrTextPipeline.RunCli();

            case "--ocr-tuning":
                return OcrTuning.RunCli();

            case "--decide":
                {
                    var txt = args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : null;
                    return ChoiceDecider.RunCli(txt, Num(args, "--act", 0), Num(args, "--team", 0), Num(args, "--target", 4));
                }

            case "--season-smoke":
                return SeasonSmoke();

            case "--flow":
                return RunFlow();

            default:
                Cli.Say("未知命令: " + args[0]);
                return Help();
        }
    }

    /// <summary>一条命令跑完跨平台全流程（云端测试用），任一步失败即返回非 0。</summary>
    private static int RunFlow()
    {
        var failed = new List<string>();
        void Step(string name, Func<int> run)
        {
            Cli.Say("");
            Cli.Say("=== " + name + " ===");
            var code = run();
            Cli.Say($"[{name}] exit={code}");
            if (code != 0) failed.Add($"{name}(exit={code})");
        }

        Step("策略清单", DecisionPolicy.RunCli);
        Step("赛季自动发现", SeasonRegistry.RunCli);
        Step("幕内知识", () => Playbook.RunCli(null));
        Step("敌人档案", () =>
        {
            DecisionPolicy.Load(AppPaths.Data("decision-policy.json"));
            Playbook.Load(AppPaths.Data("act-playbook.json"));
            return QuantData.RunEnemyCli(0, null);
        });
        Step("我方池", () =>
        {
            DecisionPolicy.Load(AppPaths.Data("decision-policy.json"));
            return QuantData.RunTeamCli();
        });
        Step("量化决策", () => QuantScorer.RunCli(0, null, null));
        Step("通用最优配队（全赛季全难度）", () => TeamSolver.RunCli("all", 0, null, 0, null));
        Step("跨幕全局分配（当前赛季）", () => GlobalAllocator.RunCli(0, null, null));
        // 把配队从"只打印"升级为"逐条断言"（B 层 → A 层，见 docs/FLOW.md §3）
        Step("配队质量闸门", () => QualityGates.RunCli(0));
        Step("OCR 引擎调优", OcrTuning.RunCli);
        Step("决策编排三态", () => DecisionOrchestrator.RunCli(0, null));
        // 真实调用模型（enabled 时）：这是 flow 之前**完全没覆盖**的一环。
        // 语义是"可观测不阻断"：拿不到就 WARN，退出码仍 0（云端网络/配额抖动不该判全流程失败）。
        Step("模型通道真实调用", () =>
        {
            using var adv = LlmAdvisor.TryLoad(AppPaths.Data("llm.json"));
            if (adv == null || !adv.Enabled) { Cli.Say("模型未启用（跳过，不算失败）"); return 0; }
            var text = adv.RefineAsync("第3幕 · 翠翎恐蕈", new[] { "雷推怒气条，满条后 15 秒力竭" }, "池子 5 人")
                .GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(text))
            {
                Cli.Say($"WARN 模型无输出（健康状态：{adv.Health}）");
                return 0;
            }
            Cli.Say($"模型健康：{adv.Health}｜输出首行：{text.Split('\n')[0].Trim()}");
            return 0;
        });

        Cli.Say("");
        if (failed.Count == 0) { Cli.Say("全流程通过 ✅"); return 0; }
        Cli.Say("失败步骤: " + string.Join(", ", failed));
        return 1;
    }

    /// <summary>
    /// 第二赛季冒烟：验证「丢一个 seasonNN.json 就能自动适配」不是口头声明。
    /// 在临时目录里复制现有 data、把 season29 改成 season30（改赛季号与元素），
    /// 然后检查自动发现、当前赛季判定、幕数推导是否跟着变。仓库文件不动。
    /// </summary>
    private static int SeasonSmoke()
    {
        var src = AppPaths.Dir;
        var tmp = Path.Combine(Path.GetTempPath(), "theater-season-smoke");
        try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        Directory.CreateDirectory(Path.Combine(tmp, "data"));
        foreach (var f in Directory.GetFiles(Path.Combine(src, "data"), "*.json"))
            File.Copy(f, Path.Combine(tmp, "data", Path.GetFileName(f)), true);

        var baseSeason = Path.Combine(tmp, "data", "season29.json");
        if (!File.Exists(baseSeason)) { Cli.Say("缺 season29.json，无法做第二赛季冒烟"); return 2; }
        var next = Path.Combine(tmp, "data", "season30.json");
        // 用 JSON 节点改写，不做字符串替换：season29.json 的元素数组无空格（["Hydro","Electro","Geo"]），
        // 带空格的字面替换匹配不上 → 冒烟误判"新元素未生效"（云端实测踩过）。
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(baseSeason));
        node["season"] = 30;
        node["elements"] = new System.Text.Json.Nodes.JsonArray("Pyro", "Cryo", "Dendro");
        File.WriteAllText(next, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        AppPaths.Init(tmp);
        var list = SeasonRegistry.Load();
        var cur = SeasonRegistry.For(0);
        var old = SeasonRegistry.For(29);
        Cli.Say($"发现 {list.Count} 个赛季｜当前第{cur?.Season}季 元素 {string.Join("/", cur?.Elements ?? new List<string>())}");
        var okCount = list.Count >= 2;
        var okCurrent = cur != null && cur.Season == 30;
        var okOld = old != null && old.Season == 29;
        var okElements = cur != null && cur.Elements.Contains("Pyro");

        QuantData.LoadEnemies(cur.Path);
        var lunarActs = QuantData.MaxAct("Lunar");
        var okActs = lunarActs > 0;

        Cli.Say($"判定：赛季数>=2 {okCount}｜当前=30 {okCurrent}｜旧赛季保留 {okOld}｜新元素生效 {okElements}｜Lunar {lunarActs} 幕 {okActs}");
        var pass = okCount && okCurrent && okOld && okElements && okActs;
        Cli.Say(pass ? "第二赛季冒烟通过 ✅（丢 seasonNN.json 即生效）" : "第二赛季冒烟失败 ❌");
        AppPaths.Init(src);   // 还原，避免影响后续
        return pass ? 0 : 1;
    }

    private static int Help()
    {
        Cli.Say(@"TheaterCli — 跨平台（Linux/Windows）决策与配队 CLI

用法:
  TheaterCli --flow                           # 一条命令跑完全流程（云端测试用）
  TheaterCli --season-smoke                   # 第二赛季冒烟（临时目录造 season30，验证自动适配）
  TheaterCli --policy                         # 决策策略（确认来自 data/decision-policy.json）
  TheaterCli --seasons                        # 赛季自动发现
  TheaterCli --playbook [--out f.md]          # 幕内知识清单
  TheaterCli --enemy-profile [--act N] [--difficulty Lunar]
  TheaterCli --team-profile                   # 我方池（有 roster 则按玩家池）
  TheaterCli --quant-decide [--act N] [--difficulty Lunar] [--out f.json]
  TheaterCli --team-solve [--difficulty all] [--season N] [--roster r.json] [--out f.json]
  TheaterCli --global-alloc [--season N] [--roster r.json] [--out f.json]
  TheaterCli --orchestrate [--act N] [--difficulty Lunar]
  TheaterCli --decide ""卡片1\n卡片2"" [--act N]

通用参数:
  --data <dir>   数据目录（默认向上查找含 data/season*.json 的根；也可用 THEATER_DATA）
");
        return 0;
    }
}
