namespace SharpRail.Host.Core;

// Terminal controls for a process whose standard streams are a terminal, such as the relay.
public static class TerminalDevice
{
    public static bool TryGetSize(int fd, out int columns, out int rows)
    {
        Posix.EnsureSupported();
        return Posix.TryGetWindowSize(fd, out columns, out rows);
    }

    // Returns a scope that restores the previous mode, or null when fd is not a terminal.
    public static IDisposable? EnterRawMode(int fd)
    {
        Posix.EnsureSupported();
        var original = new byte[256];
        if (Posix.GetAttributes(fd, original) != 0) return null;
        var raw = (byte[])original.Clone();
        Posix.MakeRaw(raw);
        if (Posix.SetAttributes(fd, 0, raw) != 0) throw new IOException("Couldn't switch the terminal to raw mode: " + Posix.LastError());
        return new Restore(fd, original);
    }

    private sealed class Restore(int fd, byte[] original) : IDisposable
    {
        private int done;
        // TCSADRAIN lets pending output reach the terminal before the mode changes back.
        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 0) Posix.SetAttributes(fd, 1, original);
        }
    }
}
