using System.Runtime.InteropServices.JavaScript;

namespace Tereditor.UI.Browser;

internal static partial class BrowserInterop
{
    [JSExport]
    public static async Task Event(string message)
    {
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (message.StartsWith("restty:", StringComparison.Ordinal)) ResttyTerminal.Event(message[7..]);
            else if (message == "viewport") BrowserSession.Current?.ViewportChanged();
            else BrowserWindows.Instance.HandleEvent(message);
        });
    }
    [JSImport("requestNotificationPermission", "tereditor")]
    internal static partial void RequestNotificationPermission();
    [JSImport("notify", "tereditor")]
    internal static partial void Notify(string id, string title, string body);
    [JSImport("createWindow", "tereditor")]
    internal static partial string CreateWindow();
    [JSImport("placeWindow", "tereditor")]
    internal static partial void PlaceWindow(string id, double width, double height, bool dialog, bool shown);
    [JSImport("windowWidth", "tereditor")]
    internal static partial double WindowWidth(string id);
    [JSImport("windowHeight", "tereditor")]
    internal static partial double WindowHeight(string id);
    [JSImport("removeWindow", "tereditor")]
    internal static partial void RemoveWindow(string id);
    [JSImport("activateWindow", "tereditor")]
    internal static partial void ActivateWindow(string id);
    [JSImport("enableWindow", "tereditor")]
    internal static partial void EnableWindow(string id, bool enabled);
    [JSImport("cutNative", "tereditor")]
    internal static partial void CutNative(string id, string rects);
    [JSImport("dimNative", "tereditor")]
    internal static partial void DimNative(string id, int red, int green, int blue, double alpha);
    [JSImport("setTitle", "tereditor")]
    internal static partial void SetTitle(string id, string title);
    [JSImport("setWorkbench", "tereditor")]
    internal static partial void SetWorkbench(string id, bool workbench);
    [JSImport("load", "tereditor")]
    internal static partial string? Load(string key);
    [JSImport("save", "tereditor")]
    internal static partial void Save(string key, string value);
    [JSImport("renderMermaid", "tereditor")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static partial Task<string> RenderMermaid(string source, string options);
    [JSImport("readPdf", "tereditor")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static partial Task<string> ReadPdf(string bytes);
    [JSImport("renderPdf", "tereditor")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    internal static partial Task<string> RenderPdf(string bytes, double scale);
    [JSImport("preload", "tereditor")]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    internal static partial Task Preload(string path);
    [JSImport("isVisible", "tereditor")]
    internal static partial bool IsVisible();
    [JSImport("origin", "tereditor")]
    internal static partial string Origin();
    [JSImport("viewportWidth", "tereditor")]
    internal static partial double ViewportWidth();
    [JSImport("isCoarse", "tereditor")]
    internal static partial bool IsCoarse();
    [JSImport("smallestScreenSide", "tereditor")]
    internal static partial double SmallestScreenSide();
    [JSImport("isMac", "tereditor")]
    internal static partial bool IsMac();
    [JSImport("openLink", "tereditor")]
    internal static partial void OpenLink(string url);
}