using Android.Gms.Tasks;

using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Xamarin.Google.MLKit.Vision.CodeScanner;

using AndroidActivity = Android.App.Activity;

namespace SharpRail.UI.Android;

/// <summary>Reads one QR code with the system's scanner, which owns the camera: the app asks for no permission.</summary>
internal static class QrScanner
{
    /// <summary>The scanned text, or null when the user backed out. Fails when the device has no scanner.</summary>
    public static Task<string?> ScanAsync(AndroidActivity activity)
    {
        var result = new TaskCompletionSource<string?>();
        var options = new GmsBarcodeScannerOptions.Builder().SetBarcodeFormats(Barcode.FormatQrCode).Build();
        GmsBarcodeScanning.GetClient(activity, options).StartScan()
            .AddOnSuccessListener(new Listener(result))
            .AddOnCanceledListener(new Listener(result))
            .AddOnFailureListener(new Listener(result));
        return result.Task;
    }

    private sealed class Listener(TaskCompletionSource<string?> result) : Java.Lang.Object, IOnSuccessListener, IOnCanceledListener, IOnFailureListener
    {
        public void OnSuccess(Java.Lang.Object? code) => result.TrySetResult((code as Barcode)?.RawValue);
        public void OnCanceled() => result.TrySetResult(null);
        public void OnFailure(Java.Lang.Exception error) => result.TrySetException(new IOException(error.Message));
    }
}