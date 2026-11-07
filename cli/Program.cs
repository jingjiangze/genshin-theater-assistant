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
                return LlmAdvisor.RunProbe(AppPaths.Data("llm.json"), Array.IndexOf(args, "--dry") >= 0);

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

            case "--global-alloc":
                return GlobalAllocator.RunCli(Num(args, "--season", 0), Opt(args, "--out"), Opt(args, "--roster"));

            case "--orchestrate":
                return DecisionOrchestrator.RunCli(Num(args, "--act", 0), Opt(args, "--difficulty"));

            case "--decide":
                {
                    var txt = args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : null;
                    return ChoiceDecider.RunCli(txt, Num(args, "--act", 0), Num(args, "--team", 0), Num(args, "--target", 4));
                }

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
        Step("决策编排三态", () => DecisionOrchestrator.RunCli(0, null));

        Cli.Say("");
        if (failed.Count == 0) { Cli.Say("全流程通过 ✅"); return 0; }
        Cli.Say("失败步骤: " + string.Join(", ", failed));
        return 1;
    }

    private static int Help()
    {
        Cli.Say(@"TheaterCli — 跨平台（Linux/Windows）决策与配队 CLI

用法:
  TheaterCli --flow                           # 一条命令跑完全流程（云端测试用）
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
