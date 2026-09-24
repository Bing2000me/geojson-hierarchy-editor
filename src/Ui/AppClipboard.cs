using Aprillz.MewUI;

namespace GeoJsonEditor.Ui;

/// <summary>
/// 系统剪贴板的文本读写。Windows 上剪贴板可能正被别的程序占用，打开失败时稍等重试几次。
/// 平台没有剪贴板服务时退回到进程内的剪贴板（同一进程的多个窗口之间仍然能复制粘贴）。
/// </summary>
public static class AppClipboard
{
    private static string? _fallback;

    // 写系统剪贴板失败时记下当时系统剪贴板里的文字：之后只要用户没有在别处复制新内容，粘贴就用进程内的这份
    private static bool _systemWriteFailed;
    private static string? _systemTextAtFailure;

    private static Aprillz.MewUI.Platform.IClipboardService? Service
        => Application.IsRunning ? Application.Current.PlatformServices.Clipboard : null;

    public static bool SetText(string text)
    {
        _fallback = text;
        _systemWriteFailed = false;
        var service = Service;
        if (service == null) return true;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (service.TrySetText(text)) return true;
            }
            catch (Exception)
            {
            }
            Thread.Sleep(30);
        }
        _systemWriteFailed = true;
        _systemTextAtFailure = ReadSystem(service);
        return false;
    }

    public static string? GetText()
    {
        var service = Service;
        if (service == null) return _fallback;
        var text = ReadSystem(service);
        if (_systemWriteFailed)
        {
            if (text == _systemTextAtFailure) return _fallback;
            _systemWriteFailed = false;
        }
        return text;
    }

    private static string? ReadSystem(Aprillz.MewUI.Platform.IClipboardService service)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (service.TryGetText(out var text)) return text;
                if (!service.HasText()) return null;
            }
            catch (Exception)
            {
            }
            Thread.Sleep(30);
        }
        return null;
    }
}
