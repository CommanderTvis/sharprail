import { dotnet } from './_framework/dotnet.js';
const runtime = await dotnet.create();
globalThis.tereditorRuntime = runtime;
await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
const { registerEvents } = await import('./tereditor.js');
registerEvents(exports.Tereditor.UI.Browser.BrowserInterop.Event);
