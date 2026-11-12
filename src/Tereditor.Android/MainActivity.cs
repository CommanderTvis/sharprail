using Android.Content.PM;

using Avalonia.Android;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Android.Windowing;
using SharpRail.UI.Terminal;

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
[global::Android.App.IntentFilter([global::Android.Content.Intent.ActionSend, global::Android.Content.Intent.ActionSendMultiple],
    Categories = [global::Android.Content.Intent.CategoryDefault], DataMimeType = "*/*", Label = "SharpRail agent")]
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
        ReceiveShare(Intent);
    }

    protected override void OnNewIntent(global::Android.Content.Intent? intent)
    {
        base.OnNewIntent(intent);
        ReceiveShare(intent);
    }

    // A share from another app (a screenshot's Share): its streams are read off the UI thread and queued for an agent terminal.
    private void ReceiveShare(global::Android.Content.Intent? intent)
    {
        if (intent?.Action is not (global::Android.Content.Intent.ActionSend or global::Android.Content.Intent.ActionSendMultiple)) return;
        var uris = new List<global::Android.Net.Uri>();
        if (intent.ClipData is { } clip)
            for (var i = 0; i < clip.ItemCount; i++) if (clip.GetItemAt(i)?.Uri is { } uri) uris.Add(uri);
        if (uris.Count == 0 && StreamExtra(intent) is global::Android.Net.Uri single) uris.Add(single);
        var text = intent.GetStringExtra(global::Android.Content.Intent.ExtraText);
        intent.SetAction(null);
        var resolver = ContentResolver!;
        _ = Task.Run(() =>
        {
            try
            {
                var entries = new List<FileTransferEntry>();
                long bytes = 0;
                foreach (var uri in uris)
                {
                    using var input = resolver.OpenInputStream(uri) ?? throw new IOException("The shared item cannot be read.");
                    using var output = new MemoryStream();
                    var buffer = new byte[81920];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        bytes += read;
                        if (bytes > FileLimits.EditableBytes) throw new IOException("Share files totalling no more than 64 MB.");
                        output.Write(buffer, 0, read);
                    }
                    entries.Add(new(SharedName(resolver, uri, entries.Count), false, output.ToArray()));
                }
                if (entries.Count == 0 && !string.IsNullOrEmpty(text)) entries.Add(new("shared.txt", false, System.Text.Encoding.UTF8.GetBytes(text)));
                SharedAttachments.Add(entries);
            }
            catch (Exception error) { global::Android.Util.Log.Warn("SharpRail", "Shared item not attached: " + error.Message); }
        });
    }

    private static Java.Lang.Object? StreamExtra(global::Android.Content.Intent intent) => OperatingSystem.IsAndroidVersionAtLeast(33)
        ? intent.GetParcelableExtra(global::Android.Content.Intent.ExtraStream, Java.Lang.Class.FromType(typeof(global::Android.Net.Uri)))
#pragma warning disable CA1422 // The typed overload exists only from Android 13.
        : intent.GetParcelableExtra(global::Android.Content.Intent.ExtraStream);
#pragma warning restore CA1422

    private static string SharedName(global::Android.Content.ContentResolver resolver, global::Android.Net.Uri uri, int index)
    {
        string? name = null;
        try
        {
            using var cursor = resolver.Query(uri, [global::Android.Provider.IOpenableColumns.DisplayName], null, null, null);
            if (cursor?.MoveToFirst() == true) name = cursor.GetString(0);
        }
        catch (Exception) { }
        name = Path.GetFileName(name ?? uri.LastPathSegment ?? "");
        if (string.IsNullOrWhiteSpace(name)) name = "shared-" + index;
        if (!Path.HasExtension(name) && resolver.GetType(uri) is { } mime &&
            global::Android.Webkit.MimeTypeMap.Singleton?.GetExtensionFromMimeType(mime) is { } extension) name += "." + extension;
        return name;
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