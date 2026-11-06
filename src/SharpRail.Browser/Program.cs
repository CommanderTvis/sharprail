using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

using Avalonia;
using Avalonia.Browser;

using SharpRail.UI.Resources.Pdf;
using SharpRail.Plugins.UI.Kit.Visualization;
using SharpRail.UI;
using SharpRail.UI.Browser;

await JSHost.ImportAsync("sharprail", "../sharprail.js");
await AppBuilder.Configure<App>().UseBrowser().AfterSetup(_ => BrowserWindows.Install()).SetupBrowserAppAsync(new BrowserPlatformOptions { PreferManagedThreadDispatcher = false });
MermaidRenderer.PlatformRender = async (source, options) =>
{
    try { return new(await BrowserInterop.RenderMermaid(source, options), null); }
    catch (JSException error) { return new(null, error.Message); }
};
PdfPlatform.Read = async bytes => JsonSerializer.Deserialize(await BrowserInterop.ReadPdf(Convert.ToBase64String(bytes)), BrowserJson.Default.PdfPlatformPageArray)!;
PdfPlatform.Render = async (bytes, scale) => JsonSerializer.Deserialize(await BrowserInterop.RenderPdf(Convert.ToBase64String(bytes), scale), BrowserJson.Default.PdfPlatformRasterArray)!;
new BrowserSession().Start();