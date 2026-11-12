using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

using Avalonia;
using Avalonia.Browser;

using Tereditor.UI.Resources.Pdf;
using Tereditor.Plugins.UI.Kit.Visualization;
using Tereditor.UI;
using Tereditor.UI.Browser;

await JSHost.ImportAsync("tereditor", "../tereditor.js");
await AppBuilder.Configure<App>().UseBrowser().AfterSetup(_ => BrowserWindows.Install()).SetupBrowserAppAsync(new BrowserPlatformOptions { PreferManagedThreadDispatcher = false });
MermaidRenderer.PlatformRender = async (source, options) =>
{
    try { return new(await BrowserInterop.RenderMermaid(source, options), null); }
    catch (JSException error) { return new(null, error.Message); }
};
PdfPlatform.Read = async bytes => JsonSerializer.Deserialize(await BrowserInterop.ReadPdf(Convert.ToBase64String(bytes)), BrowserJson.Default.PdfPlatformPageArray)!;
PdfPlatform.Render = async (bytes, scale) => JsonSerializer.Deserialize(await BrowserInterop.RenderPdf(Convert.ToBase64String(bytes), scale), BrowserJson.Default.PdfPlatformRasterArray)!;
new BrowserSession().Start();