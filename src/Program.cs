using System.Runtime.CompilerServices;

using Aprillz.MewUI;
using Aprillz.MewUI.Skia.Interop;

using GeoJsonEditor.App;
using GeoJsonEditor.Ui;

namespace GeoJsonEditor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        RegisterPlatform();

        Application.DispatcherUnhandledException += e =>
        {
            Console.Error.WriteLine(e.Exception);
            e.Handled = true;
        };

        Localization.ApplyChinese();

        // 上次自动更新留下的旧文件在后台清理；有下载好的新版本时，程序退出后替换（ProcessExit 兜底，例如从菜单退出）
        _ = Task.Run(UpdateService.Cleanup);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => UpdateService.ApplyPending();

        var settings = AppSettings.Load();
        Application
            .Create()
            .UseTheme(settings.Theme)
            .UseAccent(Color.FromRgb(0x25, 0x63, 0xEB))
            .UseSeed(
                new ThemeSeed
                {
                    WindowBackground = Color.FromRgb(0xF7, 0xF8, 0xFA),
                    WindowText = Color.FromRgb(0x1F, 0x23, 0x28),
                    ControlBackground = Color.FromRgb(0xFF, 0xFF, 0xFF),
                    ButtonFace = Color.FromRgb(0xF1, 0xF3, 0xF5),
                    ButtonDisabledBackground = Color.FromRgb(0xEC, 0xEE, 0xF1),
                },
                new ThemeSeed
                {
                    WindowBackground = Color.FromRgb(0x1A, 0x1C, 0x21),
                    WindowText = Color.FromRgb(0xE6, 0xE8, 0xEB),
                    ControlBackground = Color.FromRgb(0x24, 0x27, 0x2D),
                    ButtonFace = Color.FromRgb(0x2C, 0x30, 0x37),
                    ButtonDisabledBackground = Color.FromRgb(0x26, 0x29, 0x2E),
                })
            .UseMetrics(ThemeMetrics.Default with
            {
                FontSize = 13,
                FontSizeSmall = 11.5,
                FontSizeMedium = 14.5,
                BaseControlHeight = 30,
                ControlCornerRadius = 6,
                ControlBorderThickness = 1,
                ItemPadding = new Thickness(8, 2, 8, 2),
            })
            .WithShutdownMode(ShutdownMode.OnLastWindowClose)
            .BuildMainWindow(() => new MainWindow(settings, args))
            .Run();

        UpdateService.ApplyPending();
    }

    /// <summary>
    /// 按操作系统注册平台、渲染后端和 Skia 零拷贝互操作。
    /// 按平台发布时只会带上当前平台的程序集，所以每个平台单独一个不内联的方法，
    /// 这样 JIT 只会加载实际用到的那一组程序集。
    /// </summary>
    private static void RegisterPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            RegisterWindows();
        }
        else if (OperatingSystem.IsMacOS())
        {
            RegisterMacOS();
        }
        else if (OperatingSystem.IsLinux())
        {
            RegisterLinux();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterWindows()
    {
        Win32Platform.Register();
        Direct2DBackend.Register();
        SkiaDirect2DInterop.Register();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterMacOS()
    {
        MacOSPlatform.Register();
        MewVGMacOSBackend.Register();
        SkiaMewVGMacOSInterop.Register();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterLinux()
    {
        X11Platform.Register();
        MewVGX11Backend.Register();
        SkiaMewVGX11Interop.Register();
    }
}
