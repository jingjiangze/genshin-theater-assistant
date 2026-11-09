using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TheaterAssistant;

/// <summary>
/// 全局热键（Win32 RegisterHotKey）。不注入、不模拟输入，只注册系统级快捷键——
/// 与 WinForms 版用的是同一套 API，只是宿主换成 WPF 窗口的 HWND。
/// </summary>
internal sealed class GlobalHotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly List<int> _registered = new();
    private bool _disposed;

    /// <summary>宿主要求：必须是已经创建了 HWND 的窗口（Show() 之后）。</summary>
    public GlobalHotkeys(Window owner)
    {
        var hwnd = new WindowInteropHelper(owner).Handle;
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("热键宿主窗口还没有 HWND");
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>注册一个热键；冲突（被别的程序占用）返回 false，不抛异常。</summary>
    public bool Register(int id, uint modifiers, uint virtualKey, Action action)
    {
        if (_source == null || _disposed) return false;
        if (!RegisterHotKey(_source.Handle, id, modifiers, virtualKey)) return false;
        _actions[id] = action;
        _registered.Add(id);
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try { action(); } catch (Exception ex) { App.LogCrash(ex, "GlobalHotkeys"); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source?.RemoveHook(WndProc);
        foreach (var id in _registered)
        {
            try { UnregisterHotKey(_source?.Handle ?? IntPtr.Zero, id); } catch { }
        }
        _registered.Clear();
        _actions.Clear();
    }
}
