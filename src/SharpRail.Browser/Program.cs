using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

using Avalonia;
using Avalonia.Browser;

using SharpRail.Plugins.PdfPreview.UI;
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
PdfPlatform.Read = async bytes => JsonSerializer.Deserialize<PdfPlatformPage[]>(await BrowserInterop.ReadPdf(Convert.ToBase64String(bytes)))!;
PdfPlatform.Render = async (bytes, scale) => JsonSerializer.Deserialize<PdfPlatformRaster[]>(await BrowserInterop.RenderPdf(Convert.ToBase64String(bytes), scale))!;
new BrowserSession().Start();