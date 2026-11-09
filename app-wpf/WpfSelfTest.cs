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
            var bmp = Render(s, 720, 560);
            var nonBlank = NonBlankRatio(bmp);
            Check("SettingsWindow 实例化并渲染", nonBlank > 0.01, $"非空像素 {nonBlank:P1}");
            Save(bmp, "selftest-wpf-settings.png");
            s.Close();
        }
        catch (Exception ex)
        {
            Check("SettingsWindow 实例化并渲染", false, ex.GetType().Name + ": " + ex.Message);
        }

        log.AppendLine();
        log.AppendLine($"通过 {pass} / 失败 {fail}");
        var outPath = Path.Combine(AppPaths.Dir, "selftest-wpf-report.txt");
        try { File.WriteAllText(outPath, log.ToString(), new UTF8Encoding(false)); } catch { }
        Console.Write(log.ToString());
        return fail == 0 ? 0 : 1;
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
