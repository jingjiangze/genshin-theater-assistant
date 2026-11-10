using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
// UseWindowsForms 与 UseWPF 共存时，Application/Size 等类型会二义，显式别名到 WPF 侧

namespace TheaterAssistant;

/// <summary>
/// WPF 自检：真实实例化每个窗口并渲染成位图，把"只有运行时才炸"的问题挡在 CI 里。
///
/// 为什么必须有（本次踩过的坑）：
///   我把 `<Path Fill="{StaticResource AccentBlue}"/>` 写成了 Fill 一个 **Color** 资源，
///   而 Fill 需要 **Brush** —— 编译期完全通过，双击才弹 `XamlParseException`。
///   WPF 的 XAML 错误绝大多数属于这类"类型/资源解析在运行时才校验"的问题，
///   唯一可靠的验证方式就是**真的把窗口构造出来并渲染**。
///
/// 断言：
///   1. App.xaml 的资源字典能被解析（App 自身初始化即验证）
///   2. OverlayWindow 能实例化（XAML 中的资源引用全部可解析）
///   3. SettingsWindow 能实例化
///   4. 两者都能渲染成非空白位图（说明控件树真的建起来了）
///   5. 遮罩窗口能渲染，且日志裁剪守住 200 inline 上限（照抄 BetterGI 的判据）
///   6. 遮罩显隐策略（前台显示 / 别的程序隐藏 / 最小化保持 / 手动隐藏优先）
/// </summary>
internal static class WpfSelfTest
{
    public static int Run()
    {
        var log = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
        }

        // ⓪ 决策层初始化（自检必须在 App 的 Init 之前跑，所以这里自己先init）
        //    这样"语料库/角色池/OCR"这些真实数据链路能被断言覆盖，而不是只验窗口能不能画。
        try { DecisionBridge.Init(); } catch (Exception ex) { LogCrashLite("DecisionBridge.Init", ex); }

        // ①a 语料索引必须真的加载出内容（WPF 版曾漏调 GuideIndex.Load，显示 0 条）
        Check("语料索引已加载", GuideIndex.Count > 0, $"GuideIndex {GuideIndex.Count} 条");

        // ① 资源字典（App.xaml）已在 Application 初始化时加载，能走到这里就说明解析通过
        Check("App 资源字典可解析",
            Application.Current?.Resources.Count > 0,
            $"资源数 {Application.Current?.Resources.Count ?? 0}");

        // ② 悬浮窗实例化 + 渲染
        try
        {
            var w = new OverlayWindow();
            w.Show();
            var bmp = Render(w, 640, 300);
            var nonBlank = NonBlankRatio(bmp);
            Check("OverlayWindow 实例化并渲染", nonBlank > 0.01, $"非空像素 {nonBlank:P1}");
            Save(bmp, "selftest-wpf-overlay.png");
            w.Close();
        }
        catch (Exception ex)
        {
            Check("OverlayWindow 实例化并渲染", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ②b 桌宠窗口（收起态）：动画与气泡是运行时逻辑，这里只验它能建起来并画出形象
        try
        {
            var pet = new PetWindow();
            pet.Show();
            pet.SetState(PetWindow.PetState.Done);
            pet.ShowAdvice("第 6 幕 · 困难",
                new[] { ("◆ 建议：五郎/九条裟罗/伊安珊", "量化"), ("★ 云端已验证：第3幕推荐 爱诺", "已验证") });
            var pb = Render(pet, 260, 360);
            var pn = NonBlankRatio(pb);
            Check("PetWindow 实例化并渲染", pn > 0.01, $"非空像素 {pn:P1}");
            Save(pb, "selftest-wpf-pet.png");
            pet.Close();
        }
        catch (Exception ex)
        {
            Check("PetWindow 实例化并渲染", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ②c 悬浮球（常态形态，照 MapsBalloon 的 76×76）
        try
        {
            var ball = new FloatingBall();
            ball.Show();
            ball.SetState(FloatingBall.BallState.Done);
            var bb = Render(ball, 76, 76);
            var bn = NonBlankRatio(bb);
            Check("FloatingBall 实例化并渲染", bn > 0.01, $"非空像素 {bn:P1}");
            Save(bb, "selftest-wpf-ball.png");
            ball.Close();
        }
        catch (Exception ex)
        {
            Check("FloatingBall 实例化并渲染", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ③ 设置窗口实例化 + 渲染
        try
        {
            var s = new SettingsWindow();
            s.Show();

            // 导航是 Fluent 形态（左导航 + 卡片页）：页数固定 7，切页走动画路径不能炸
            var navOk = s.PageCount == 7;
            try { s.SelectPage(3); } catch { navOk = false; }

            var bmp = Render(s, 880, 620);
            var nonBlank = NonBlankRatio(bmp);
            Check("SettingsWindow 实例化并渲染", nonBlank > 0.01, $"非空像素 {nonBlank:P1}");
            Check("设置页导航（7 页 + 切页）", navOk, $"PageCount={s.PageCount}");
            Save(bmp, "selftest-wpf-settings.png");
            s.Close();
        }
        catch (Exception ex)
        {
            Check("SettingsWindow 实例化并渲染", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ③b 遮罩窗口（照抄 BetterGI 的结构）：日志裁剪 + 识别框 + 状态胶囊
        try
        {
            var mask = new MaskWindow();
            mask.Show();
            mask.SetStatus("第3幕", "★云端已验证");
            mask.SetAdvice("用雷把怒气条推满，满条后 15 秒内把爆发全打出去");
            mask.SetGuide("手法·标记期奶满或无敌帧躲，解除后才是输出窗口（伤害 -50%/暴击 -100%）");

            // 300 行远超上限：裁剪后 inline 数必须 ≤ 200（与上游同一条判据）
            for (var i = 0; i < 300; i++)
                mask.AppendLog(i % 7 == 0 ? "WRN" : "INF", $"第 {i} 行：合成日志，用于验证裁剪上限");

            var inlines = mask.TrimLogsNow();
            Check("遮罩日志有上限", inlines <= OverlayLogDocument.MaximumInlines,
                $"inline {inlines} / 上限 {OverlayLogDocument.MaximumInlines}；已写入 {mask.LogLineCount} 行");

            mask.SetFrame(new[]
            {
                new MaskBox { Bounds = new Rect(96, 118, 180, 64), Label = "act3 0.97", Color = Color.FromRgb(0x7E, 0xD8, 0xA0) },
                new MaskBox { Bounds = new Rect(360, 596, 588, 36), Label = "幕次条带", CornersOnly = false, LabelAlignRight = true },
                new MaskBox { Bounds = new Rect(980, 300, 96, 96), Label = "hall 0.91", Color = Color.FromRgb(0xD6, 0xB6, 0x60) },
            }, 1280, 720);

            var mb = Render(mask, 1280, 720);
            var mn = NonBlankRatio(mb);
            Check("MaskWindow 实例化并渲染", mn > 0.01, $"非空像素 {mn:P1}");
            Save(mb, "selftest-wpf-mask.png");       // 生产默认形态：只有「现在做」

            // 展开日志再渲染一张：验收时两张一起看（默认形态 / 排查形态）
            mask.SetLogExpanded(true);
            var mb2 = Render(mask, 1280, 720);
            Check("遮罩日志可展开", NonBlankRatio(mb2) > mn, $"展开后非空像素 {NonBlankRatio(mb2):P1} > 收起 {mn:P1}");
            Save(mb2, "selftest-wpf-mask-log.png");
            mask.Close();
        }
        catch (Exception ex)
        {
            Check("MaskWindow 实例化并渲染", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ③c 遮罩显隐策略（纯函数：上游把这段埋在 EvaluateLocked 里，没法单独测）
        try
        {
            var bounds = new GameWindow.RECT { Left = 100, Top = 50, Right = 1380, Bottom = 770 };
            var active = new MaskGameState(new IntPtr(1), bounds, active: true, minimized: false, fgOwned: true, capturing: true);
            var otherApp = new MaskGameState(new IntPtr(1), bounds, active: false, minimized: false, fgOwned: false, capturing: true);
            var minimized = new MaskGameState(new IntPtr(1), bounds, active: false, minimized: true, fgOwned: false, capturing: true);
            var gone = new MaskGameState(IntPtr.Zero, default, active: false, minimized: false, fgOwned: false, capturing: false);

            var ok = MaskPolicy.Decide(active, false) == true
                     && MaskPolicy.Decide(otherApp, false) == false
                     && MaskPolicy.Decide(minimized, false) == null      // 最小化时保持现状，避免闪烁
                     && MaskPolicy.Decide(gone, false) == false
                     && MaskPolicy.Decide(active, true) == false;        // 手动隐藏优先于一切

            Check("遮罩显隐策略", ok,
                $"前台={MaskPolicy.Decide(active, false)} 别的程序={MaskPolicy.Decide(otherApp, false)} " +
                $"最小化={MaskPolicy.Decide(minimized, false)?.ToString() ?? "null"} " +
                $"无窗口={MaskPolicy.Decide(gone, false)} 手动隐藏={MaskPolicy.Decide(active, true)}");
        }
        catch (Exception ex)
        {
            Check("遮罩显隐策略", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ③d 玩法档案（剧诗 / 深渊）：区域数值必须是改造前那四个常量，否则识别位置会悄悄漂
        try
        {
            GameModes.Init();
            var th = GameModes.Get("theater");
            var ab = GameModes.Get("abyss");

            var bandOk = th != null
                         && Math.Abs(th.BandLeft - 0.28) < 1e-9
                         && Math.Abs(th.BandTop - 0.828) < 1e-9
                         && Math.Abs(th.BandWidth - 0.46) < 1e-9
                         && Math.Abs(th.BandHeight - 0.05) < 1e-9;

            var parseOk = th?.ParseAct("第7幕") == 7
                          && th.ParseAct("第十二幕") == 12
                          && th.ParseAct("第 3 幕 · 困难") == 3
                          && ab?.ParseAct("第12层") == 12
                          && ab.ParseAct("第7幕") == null;

            Check("玩法档案可用", bandOk && parseOk,
                $"默认={GameModes.Default?.Id} 共{GameModes.All.Count}个 条带={th?.BandLeft},{th?.BandTop},{th?.BandWidth},{th?.BandHeight} " +
                $"剧诗解析={th?.ParseAct("第7幕")}/{th?.ParseAct("第十二幕")} 深渊解析={ab?.ParseAct("第12层")}");
        }
        catch (Exception ex)
        {
            Check("玩法档案可用", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ③e 战斗中的手法指导（rotations.json → Routine.LinesFor）：WPF 生产路径此前缺失（审计发现）
        try
        {
            Routine.Load(AppPaths.Data("rotations.json"));
            var tech = Routine.LinesFor(3, 3).FirstOrDefault(l => l.StartsWith("手法·"));
            Check("战斗手法数据可用", Routine.Loaded && !string.IsNullOrEmpty(tech),
                $"acts={Routine.Count}｜act3={tech ?? "无"}");
        }
        catch (Exception ex)
        {
            Check("战斗手法数据可用", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ③f 内嵌小模型（GGUF + llama.cpp 进程内）：「随包能用」必须被断言护住
        try
        {
            var brain = LocalBrain.TryLoad(AppPaths.LocalModelPath, threads: 4, contextSize: 512);
            var ans = brain?.Ask("我在剧诗第3幕，难度月谕。一句话概括该干嘛。", 48, 4000);
            Check("内嵌模型可用（GGUF 进程内）", brain is { Ready: true } && !string.IsNullOrWhiteSpace(ans),
                brain == null ? "GGUF 未找到/加载失败：" + AppPaths.LocalModelPath + "←" + LocalBrain.LastLoadError
                              : $"{brain.Status}｜{brain.LastLatencyMs}ms｜{(ans ?? "空").Trim()}");
            try { brain?.Dispose(); } catch { }
        }
        catch (Exception ex)
        {
            Check("内嵌模型可用（GGUF 进程内）", false, ex.GetType().Name + ": " + ex.Message);
        }

        log.AppendLine();
        log.AppendLine($"通过 {pass} / 失败 {fail}");
        var outPath = Path.Combine(AppPaths.Dir, "selftest-wpf-report.txt");
        try { File.WriteAllText(outPath, log.ToString(), new UTF8Encoding(false)); } catch { }
        Console.Write(log.ToString());
        return fail == 0 ? 0 : 1;
    }

    private static void LogCrashLite(string where, Exception ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(App.LogDir);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(App.LogDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log"),
                $"{DateTime.Now:O}\r\nSOURCE: {where}\r\n{ex}\r\n");
        }
        catch { }
    }

    private static BitmapSource Render(Window w, int width, int height)
    {
        // 必须先固定不透明度：悬浮球/桌宠是 opacity 0→1 淡入的，
        // 自检在动画跑完前渲染会得到全透明位图（0.0% 非空，本次踩过）。
        try
        {
            w.BeginAnimation(Window.OpacityProperty, null);
            w.Opacity = 1.0;
        }
        catch { }

        w.Width = width;
        w.Height = height;
        w.Measure(new Size(width, height));
        w.Arrange(new Rect(0, 0, width, height));
        w.UpdateLayout();

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(w);
        rtb.Freeze();
        return rtb;
    }

    private static double NonBlankRatio(BitmapSource bmp)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        if (w <= 0 || h <= 0) return 0;
        var stride = w * 4;
        var buf = new byte[stride * h];
        bmp.CopyPixels(buf, stride, 0);

        int nonBlank = 0, sampled = 0;
        for (int y = 0; y < h; y += 2)
        {
            for (int x = 0; x < w; x += 2)
            {
                int i = y * stride + x * 4;
                int a = buf[i + 3], b = buf[i], g = buf[i + 1], r = buf[i + 2];
                sampled++;
                // 非全透明且不是纯黑，算"画了东西"
                if (a > 20 && (r + g + b) > 24) nonBlank++;
            }
        }
        return sampled == 0 ? 0 : (double)nonBlank / sampled;
    }

    private static void Save(BitmapSource bmp, string name)
    {
        try
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(AppPaths.Dir, name));
            enc.Save(fs);
        }
        catch { }
    }
}
