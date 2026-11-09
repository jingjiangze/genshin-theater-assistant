// ─────────────────────────────────────────────────────────────────────────────
// 从 app/BatchRunner.cs 抽出：Cli 是纯逻辑（控制台输出/重定向），
// 但 BatchRunner 依赖 System.Drawing（UI 层）。WPF 版需要 Cli 却不该拖进 Drawing 依赖，故独立成文件。
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.IO;

namespace TheaterAssistant;

/// <summary>控制台桥：WinExe 默认没有 stdout，从终端启动时挂到父进程控制台再打印。</summary>
internal static class Cli
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();

    public static void Attach()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
            {
                FreeConsole();
                AttachConsole(ATTACH_PARENT_PROCESS);
            }
        }
        catch { }
    }

    public static void Say(string line)
    {
        try { Console.WriteLine(line); } catch { }
    }
}
