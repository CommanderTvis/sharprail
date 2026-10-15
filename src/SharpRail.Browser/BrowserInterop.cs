using System.Runtime.InteropServices.JavaScript;

namespace SharpRail.UI.Browser;

internal static partial class BrowserInterop
{
    [JSExport]
    public static async Task Event(string message)
    {
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (message.StartsWith("restty:", StringComparison.Ordinal)) ResttyTerminal.Event(message[7..]);
            else BrowserWindows.Instance.HandleEvent(message);
        });
    }
    [JSImport("requestNotificationPermission", "sharprail")]
    internal static partial void RequestNotificationPermission();
    [JSImport("notify", "sharprail")]
    internal static partial void Notify(string id, string title, string body);
    [JSImport("createWindow", "sharprail")]
    internal static partial string CreateWindow();
    [JSImport("placeWindow", "sharprail")]
    internal static partial void PlaceWindow(string id, double width, double height, bool dialog, bool shown);
    [JSImport("windowWidth", "sharprail")]
    internal static partial double WindowWidth(string id);
    [JSImport("windowHeight", "sharprail")]
    internal static partial double WindowHeight(string id);
    [JSImport("removeWindow", "sharprail")]
    internal static partial void RemoveWindow(string id);
    [JSImport("activateWindow", "sharprail")]
    internal static partial void ActivateWindow(string id);
    [JSImport("enableWindow", "sharprail")]
    internal static partial void EnableWindow(string id, bool enabled);
    [JSImport("dimNative", "sharprail")]
    internal static partial void DimNative(string id, int red, int green, int blue, double alpha);
    [JSImport("setTitle", "sharprail")]
    internal static partial void SetTitle(string id, string title);
    [JSImport("setWorkbench", "sharprail")]
    internal static partial void SetWorkbench(string id, bool workbench);
    [JSImport("load", "sharprail")]
    internal static partial string? Load(string key);
    [JSImport("save", "sharprail")]
    internal static partial void Save(string key, string value);
    [JSImport("renderMermaid", "sharprail")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static partial Task<string> RenderMermaid(string source, string options);
    [JSImport("readPdf", "sharprail")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static partial Task<string> ReadPdf(string bytes);
    [JSImport("renderPdf", "sharprail")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static partial Task<string> RenderPdf(string bytes, double scale);
    [JSImport("isVisible", "sharprail")]
    internal static partial bool IsVisible();
    [JSImport("origin", "sharprail")]
    internal static partial string Origin();
    [JSImport("isMac", "sharprail")]
    internal static partial bool IsMac();
    [JSImport("openLink", "sharprail")]
    internal static partial void OpenLink(string url);
}