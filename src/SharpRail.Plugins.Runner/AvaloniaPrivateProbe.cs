using Avalonia;
using Avalonia.Platform;

using SharpRail.Plugins.Isolation;

namespace SharpRail.Plugins.Runner;

/// <summary>
/// Registers a stub windowing platform the way a custom remote backend would (an [Unstable] interface bound through the
/// [PrivateApi] locator) and checks that Avalonia resolves it. That it compiles at all is the finding the spec records.
/// </summary>
internal static class AvaloniaPrivateProbe
{
    internal static int Run()
    {
        var stub = new StubWindowingPlatform();
        AvaloniaLocator.CurrentMutable.Bind<IWindowingPlatform>().ToConstant(stub);
        var resolved = ReferenceEquals(AvaloniaLocator.Current.GetService<IWindowingPlatform>(), stub);
        Console.WriteLine(resolved ? RunnerProbe.AvaloniaPrivateResolved : "avalonia-private: the stub IWindowingPlatform did not resolve");
        return resolved ? 0 : 1;
    }

    private sealed class StubWindowingPlatform : IWindowingPlatform
    {
        public IWindowImpl CreateWindow() => throw new NotSupportedException();
        public IWindowImpl CreateEmbeddableWindow() => throw new NotSupportedException();
        public ITopLevelImpl CreateEmbeddableTopLevel() => throw new NotSupportedException();
        public ITrayIconImpl? CreateTrayIcon() => null;
        public void GetWindowsZOrder(ReadOnlySpan<IWindowImpl> windows, Span<long> zOrder) => throw new NotSupportedException();
    }
}