import { dotnet } from './_framework/dotnet.js';
const runtime = await dotnet.create();
await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
const { registerEvents } = await import('./sharprail.js');
registerEvents(exports.SharpRail.UI.Browser.BrowserInterop.Event);
