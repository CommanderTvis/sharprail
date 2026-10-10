using Android.Content.PM;

using Avalonia.Android;

using SharpRail.UI.Android.Windowing;

namespace SharpRail.UI.Android;

/// <summary>
/// The app's one activity. It handles every configuration change itself, so a rotation or a keyboard resizes the
/// workbench instead of recreating it.
/// </summary>
[global::Android.App.Activity(Label = "SharpRail", Theme = "@style/SharpRail", Icon = "@mipmap/icon", MainLauncher = true, Exported = true,
    LaunchMode = LaunchMode.SingleTask, WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize |
        ConfigChanges.UiMode | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation | ConfigChanges.Density |
        ConfigChanges.FontScale | ConfigChanges.Locale | ConfigChanges.LayoutDirection)]
public sealed class MainActivity : AvaloniaActivity
{
    private ClientSession? session;

    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AndroidWindows.Instance.Attach(this);
        BackRequested += (_, e) => e.Handled = AndroidWindows.Instance.Back() || MoveTaskToBack(true);
        session = new ClientSession(FilesDir!.AbsolutePath, compact: Resources!.Configuration!.SmallestScreenWidthDp < 600);
        session.Start();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (session is not null) session.Foreground = true;
    }

    protected override void OnPause()
    {
        if (session is not null) session.Foreground = false;
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        session?.Dispose();
        session = null;
        AndroidWindows.Instance.Detach(this);
        base.OnDestroy();
    }
}