using Android.Runtime;

using Avalonia;

using SharpRail.UI.Android.Windowing;

namespace SharpRail.UI.Android;

/// <summary>Starts Avalonia with the app's windows before any activity exists; the activity composes the workbench.</summary>
[global::Android.App.Application]
public sealed class MainApplication(nint handle, JniHandleOwnership ownership) : global::Android.App.Application(handle, ownership)
{
    public override void OnCreate()
    {
        base.OnCreate();
        AppBuilder.Configure<App>().UseAndroid().AfterPlatformServicesSetup(_ => AndroidWindows.Install()).LogToTrace().SetupWithoutStarting();
    }
}