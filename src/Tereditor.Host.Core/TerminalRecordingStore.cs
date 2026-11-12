namespace SharpRail.Host.Core;

// Last recorded screens of terminal sessions, one file per session id, so a restarted host can show them again.
// Everything is best effort: a failed or corrupt file loses a picture, never a session.
internal sealed class TerminalRecordingStore(string directory)
{
    internal const int MaxEntries = 256;
    // A snapshot is the recorded bytes, at most the largest replay size, plus a short mode preamble.
    internal const int MaxBytes = TerminalRecorder.MaxSnapshotBytes + 1024;

    internal void Save(string sessionId, byte[] recording)
    {
        if (recording.Length == 0 || recording.Length > MaxBytes) return;
        var path = PathFor(sessionId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(temporary, recording);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception) { }
        }
    }

    internal byte[]? Load(string sessionId)
    {
        try
        {
            var info = new FileInfo(PathFor(sessionId));
            return info.Exists && info.Length is > 0 and <= MaxBytes ? File.ReadAllBytes(info.FullName) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal void Delete(string sessionId)
    {
        try { File.Delete(PathFor(sessionId)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // The newest recordings up to the entry cap; older ones are left behind and removed.
    internal List<(string Id, byte[] Bytes)> LoadAll()
    {
        var result = new List<(string, byte[])>();
        try
        {
            if (!Directory.Exists(directory)) return result;
            var files = new DirectoryInfo(directory).GetFiles("*.rec").OrderByDescending(file => file.LastWriteTimeUtc).ToList();
            foreach (var file in files)
            {
                var id = Path.GetFileNameWithoutExtension(file.Name);
                if (result.Count < MaxEntries && Decode(id) is { } sessionId && Load(sessionId) is { } bytes) result.Add((sessionId, bytes));
                else try { file.Delete(); } catch (Exception) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return result;
    }

    private string PathFor(string sessionId) => Path.Combine(directory, Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(sessionId)) + ".rec");

    private static string? Decode(string hex)
    {
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromHexString(hex)); }
        catch (FormatException) { return null; }
    }
}