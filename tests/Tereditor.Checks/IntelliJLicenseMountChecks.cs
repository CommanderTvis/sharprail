using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.Lsp.IntelliJ;

namespace SharpRail.Checks;

/// <summary>
/// A mounted offline activation download (synthetic: made-up ids and payloads in the real shape) becomes the file the server reads, written under the cache and
/// never under the mount, and neither the code nor the account appears in the status, the hold text, the launch environment or the arguments.
/// </summary>
internal static class IntelliJLicenseMountChecks
{
    private const string Email = "someone@example.invalid";
    private static readonly DateOnly Today = new(2026, 11, 10);

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Session : ILanguageServerSession
    {
        internal string? Published;
        internal string? Unavailable;
        public string Workspace => "/fake";
        public bool SupportsCommand(string command) => false;
        public void SetProgress(string key, string? message) { }
        public void Track(string key, string describe, Func<CancellationToken, Task> work) { }
        public bool Contains(string path) => false;
        public void SetState(string? state) => Published = state;
        public void SetUnavailable(string? reason) => Unavailable = reason;
        public Task<JsonNode?> RequestAsync(string method, object? parameters, CancellationToken cancellationToken, TimeSpan? timeout = null) => Task.FromResult<JsonNode?>(null);
    }

    /// <summary>A code of the downloaded shape: <c>ID-base64(json)-base64-base64</c>, with a made-up payload.</summary>
    private static string MakeCode(string id, string products = "[{\"code\":\"II\",\"fallbackDate\":\"2025-01-01\",\"paidUpTo\":\"2027-01-01\"}]", string? json = null) =>
        id + "-" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json ?? $"{{\"licenseId\":\"{id}\",\"licenseeName\":\"Fixture\",\"products\":{products}}}")) + "-QUJDREVGR0hJSktMTU5PUA==-U1lOVEhFVElDUEFZTE9BRA==";

    private static string LaterName(string id) => $"{id} - {Email} - for 2018.2 or later.txt";

    private static string EarlierName(string id) => $"{id} - {Email} - for 2018.1 or earlier.txt";

    private static byte[] MakeZip(string id, string later, string earlier)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var (name, text) in new[] { (EarlierName(id), earlier), (LaterName(id), later) })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }
        return memory.ToArray();
    }

    private static string Fresh(string root, string name)
    {
        var path = Path.Combine(root, name);
        if (Directory.Exists(path)) { LockDown(path, false); Directory.Delete(path, true); }
        Directory.CreateDirectory(path);
        return path;
    }

    private static void LockDown(string path, bool readOnly)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, readOnly ? UnixFileMode.UserRead | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string[] Snapshot(string directory) => Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

    internal static void Run(string root)
    {
        var scratch = Fresh(root, "intellij-license-mount");
        try
        {
            var saved = IntelliJLicensing.EnvironmentLicense();
            Require(saved is null, "The check needs an environment without an environment license; unset " + saved + ".");
            Func<string, string?> noEnvironment = _ => null;
            var secrets = new List<string>();
            var logs = new List<string> { Email };

            // The downloaded zip as it is: the entry for 2018.2 or later is chosen, the one for 2018.1 or earlier ignored.
            var wanted = MakeCode("0TESTLIC00");
            var old = MakeCode("0TESTOLD00");
            secrets.AddRange([wanted, old]);
            var zipMount = Fresh(scratch, "zip-mount");
            File.WriteAllBytes(Path.Combine(zipMount, $"0TESTLIC00 - {Email}.zip"), MakeZip("0TESTLIC00", wanted, old));
            LockDown(zipMount, true);
            var before = Snapshot(zipMount);
            var session = Fresh(scratch, "zip-session");
            var environment = IntelliJLicenseMount.Prepare(session, noEnvironment, zipMount, Today);
            var file = environment[IntelliJLicenseMount.FileVariable] ?? throw new InvalidOperationException("The zip produced no licence file.");
            Require(File.ReadAllText(file) == wanted && IntelliJDistribution.Contains(session, file), "The zip entry for 2018.2 or later was not written under the session directory: " + IntelliJLicenseMount.Current);
            Require(IntelliJLicenseMount.Current is { Kind: "provided", LicenseId: "0TESTLIC00", Injected: true }, "A good zip was not reported as provided: " + IntelliJLicenseMount.Current);
            Require(Snapshot(zipMount).SequenceEqual(before) && !IntelliJDistribution.Contains(zipMount, file), "The mount was written to or the licence file landed inside it.");
            if (!OperatingSystem.IsWindows())
                Require((File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0, "The licence file is readable or writable by others.");
            Require(IntelliJLicensing.EnvironmentLicense() == IntelliJLicenseMount.FileVariable, "The injected licence was not reported as an environment license, so activation actions would not refuse.");
            Require(environment.Values.All(value => value is null || !secrets.Any(secret => value.Contains(secret, StringComparison.Ordinal))), "The code went into the launch environment.");
            LockDown(zipMount, false);

            // The status and the hold text carry the licence id and no code or account.
            var view = new Session();
            IntelliJLicensing.OnMessage(view, IntelliJLicensing.StateNotification, new JsonObject { ["featuresDeclined"] = true, ["parkedReason"] = "StartedUnlicensed" });
            Require(view.Published!.Contains("mountedLicense", StringComparison.Ordinal) && view.Published.Contains("0TESTLIC00", StringComparison.Ordinal) && view.Unavailable!.Contains("0TESTLIC00", StringComparison.Ordinal),
                "The status did not show the mounted licence: " + view.Published);
            logs.AddRange([view.Published!, view.Unavailable!, IntelliJLicenseMount.Current!.Message]);

            // The extracted text files: the one for 2018.2 or later wins even beside the older one.
            var textMount = Fresh(scratch, "text-mount");
            File.WriteAllText(Path.Combine(textMount, LaterName("0TESTLIC00")), wanted);
            File.WriteAllText(Path.Combine(textMount, EarlierName("0TESTLIC00")), old);
            Require(File.ReadAllText(IntelliJLicenseMount.Prepare(Fresh(scratch, "text-session"), noEnvironment, textMount, Today)[IntelliJLicenseMount.FileVariable]!) == wanted, "The extracted text file for 2018.2 or later was not chosen.");

            // A bare file (with a trailing newline an editor adds) and a name given through the variable, as file and as directory.
            var bare = Path.Combine(scratch, "bare.txt");
            File.WriteAllText(bare, wanted + "\n");
            Require(File.ReadAllText(IntelliJLicenseMount.Prepare(Fresh(scratch, "bare-session"), noEnvironment, bare, Today)[IntelliJLicenseMount.FileVariable]!) == wanted, "A bare file was not accepted or was not cleaned.");
            Func<string, string?> named(string path) => name => name == IntelliJLicenseMount.FileVariable ? path : null;
            Require(IntelliJLicenseMount.Prepare(Fresh(scratch, "named-session"), named(zipMount), "/license-absent-for-check", Today)[IntelliJLicenseMount.FileVariable] is { } viaVariable && File.ReadAllText(viaVariable) == wanted,
                "INTELLIJ_SERVER_LICENSE_FILE naming a directory did not work.");

            // Nothing mounted: nothing happens and nothing is claimed.
            Require(IntelliJLicenseMount.Prepare(Fresh(scratch, "none-session"), noEnvironment, Path.Combine(scratch, "absent"), Today).Count == 0 && IntelliJLicenseMount.Current is null, "An absent mount changed the launch.");

            // Several licences: the one for IntelliJ IDEA is chosen, else the choice is reported by id.
            var other = MakeCode("0TESTWEB00", "[{\"code\":\"WS\",\"paidUpTo\":\"2027-01-01\"}]");
            var several = Fresh(scratch, "several");
            File.WriteAllText(Path.Combine(several, LaterName("0TESTLIC00")), wanted);
            File.WriteAllText(Path.Combine(several, LaterName("0TESTWEB00")), other);
            Require(File.ReadAllText(IntelliJLicenseMount.Prepare(Fresh(scratch, "several-session"), noEnvironment, several, Today)[IntelliJLicenseMount.FileVariable]!) == wanted, "The licence for IntelliJ IDEA was not preferred.");
            File.WriteAllText(Path.Combine(several, LaterName("0TESTTWO00")), MakeCode("0TESTTWO00"));
            var ambiguous = IntelliJLicenseMount.Prepare(Fresh(scratch, "ambiguous-session"), noEnvironment, several, Today);
            Require(ambiguous.Count == 0 && IntelliJLicenseMount.Current is { Kind: "ambiguous", Injected: false } picked && picked.Message.Contains("0TESTLIC00", StringComparison.Ordinal) && picked.Message.Contains("0TESTTWO00", StringComparison.Ordinal)
                && !picked.Message.Contains(Email, StringComparison.Ordinal), "Two licences for IntelliJ IDEA were not reported as ambiguous by id: " + IntelliJLicenseMount.Current);

            // Malformed: wrong shape, bad JSON head, an empty directory, a zip without the entry. None echoes its content or file names.
            var junk = "NOTALICENCE-secret-material-xyz";
            secrets.Add(junk);
            foreach (var (name, text) in new[] { ("shape", junk), ("json", MakeCode("0TESTBAD00", json: "{\"other\":1}")), ("empty", "") })
            {
                var directory = Fresh(scratch, "bad-" + name);
                File.WriteAllText(Path.Combine(directory, LaterName("0TESTBAD00")), text);
                var result = IntelliJLicenseMount.Prepare(Fresh(scratch, "bad-session-" + name), noEnvironment, directory, Today);
                Require(result.Count == 0 && IntelliJLicenseMount.Current is { Kind: "invalid", Injected: false } bad && !secrets.Any(secret => bad.Message.Contains(secret, StringComparison.Ordinal)) && !bad.Message.Contains(Email, StringComparison.Ordinal) && !bad.Message.Contains("secret-material", StringComparison.Ordinal),
                    $"A malformed code ({name}) was handed over or echoed: " + IntelliJLicenseMount.Current);
                logs.Add(IntelliJLicenseMount.Current!.Message);
            }
            var noEntry = Fresh(scratch, "no-entry");
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true)) zip.CreateEntry("readme.txt");
                File.WriteAllBytes(Path.Combine(noEntry, "download.zip"), memory.ToArray());
            }
            Require(IntelliJLicenseMount.Prepare(Fresh(scratch, "no-entry-session"), noEnvironment, noEntry, Today).Count == 0 && IntelliJLicenseMount.Current?.Kind == "invalid", "A zip without the 2018.2 entry was accepted.");
            Require(IntelliJLicenseMount.Prepare(Fresh(scratch, "empty-session"), noEnvironment, Fresh(scratch, "empty-dir"), Today).Count == 0 && IntelliJLicenseMount.Current?.Kind == "invalid", "An empty directory was not reported.");
            // A directory the variable names that is unusable must not leave the server pointed at it.
            Require(IntelliJLicenseMount.Prepare(Fresh(scratch, "bad-named-session"), named(Path.Combine(scratch, "bad-shape")), "/license-absent-for-check", Today) is { } removal && removal.ContainsKey(IntelliJLicenseMount.FileVariable) && removal[IntelliJLicenseMount.FileVariable] is null,
                "An unusable named directory stayed in the launch environment.");

            // Expired, said plainly, and still handed over for the server's own verdict.
            var expired = MakeCode("0TESTEXP00", "[{\"code\":\"II\",\"fallbackDate\":\"2019-01-01\",\"paidUpTo\":\"2020-01-01\"}]");
            var expiredMount = Fresh(scratch, "expired");
            File.WriteAllText(Path.Combine(expiredMount, LaterName("0TESTEXP00")), expired);
            Require(IntelliJLicenseMount.Prepare(Fresh(scratch, "expired-session"), noEnvironment, expiredMount, Today).Count == 1 && IntelliJLicenseMount.Current is { Kind: "expired", LicenseId: "0TESTEXP00" }, "An expired licence was not reported: " + IntelliJLicenseMount.Current);

            // The server's own variables win and the mount is ignored with a word about it.
            Require(IntelliJLicenseMount.Prepare(Fresh(scratch, "env-session"), name => name == "INTELLIJ_SERVER_LICENSE" ? "x" : null, zipMount, Today).Count == 0 && IntelliJLicenseMount.Current?.Kind == "overridden", "INTELLIJ_SERVER_LICENSE did not win over the mount.");
            Require(IntelliJLicenseMount.Prepare(Fresh(scratch, "server-session"), name => name == "JETBRAINS_LICENSE_SERVER" ? "https://example.invalid" : null, zipMount, Today).Count == 0 && IntelliJLicenseMount.Current?.Kind == "overridden", "JETBRAINS_LICENSE_SERVER did not win over the mount.");

            // A bare named file the server may know better than this check is left alone.
            var strange = Path.Combine(scratch, "strange.key");
            File.WriteAllText(strange, "some-other-format");
            Require(IntelliJLicenseMount.Prepare(Fresh(scratch, "strange-session"), named(strange), "/license-absent-for-check", Today).Count == 0 && IntelliJLicenseMount.Current?.Kind == "unchecked", "An unfamiliar named file was withheld or replaced.");

            // Nowhere did a code or the account appear.
            foreach (var text in logs.Skip(1))
                Require(!secrets.Any(secret => text.Contains(secret, StringComparison.Ordinal)) && !text.Contains(Email, StringComparison.Ordinal), "A code or the account appeared in a message: " + text);
        }
        finally
        {
            IntelliJLicenseMount.Current = null;
            LockDown(scratch, false);
        }
    }
}