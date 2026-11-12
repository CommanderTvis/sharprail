using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    public async ValueTask<string> SavePromptImageAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (data.Length > FileLimits.PreviewBytes || data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new ArgumentException("Attach a PNG image no larger than 8 MB.");
        if (state?.DirectoryPath is not { } stateDirectory) throw new InvalidOperationException("This host has no clipboard directory.");
        var directory = Path.Combine(stateDirectory, "clipboard");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(path, data, cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }
}