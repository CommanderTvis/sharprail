using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tereditor.Plugins.Lsp.IntelliJ;

/// <summary>What became of a licence directory or file: a kind, the licence id (never the code or the account) and a sentence for the user.</summary>
/// <param name="Kind"><c>provided</c>, <c>expired</c> (still provided), <c>invalid</c>, <c>ambiguous</c>, <c>unreadable</c>, <c>unchecked</c> or <c>overridden</c>.</param>
/// <param name="LicenseId">The licence id, when one was chosen.</param>
/// <param name="Message">Plain words without the code, file names or the account.</param>
/// <param name="Injected">Whether the server was told to read a licence file this launch wrote.</param>
internal sealed record MountedLicense(string Kind, string? LicenseId, string Message, bool Injected);

/// <summary>
/// The offline activation code a user downloads from account.jetbrains.com (a zip with two one-line text files) mounted as <c>/license</c>, or named by
/// <c>INTELLIJ_SERVER_LICENSE_FILE</c> as a file, a directory or the zip. The server documents <c>INTELLIJ_SERVER_LICENSE_FILE</c> as a file holding the key
/// text, read into memory and authoritative, so the chosen code is written to a private file in the launch's session directory and only that path goes into the
/// launch environment. The mount itself is never written to; the code is never logged or put in arguments.
/// </summary>
internal static class IntelliJLicenseMount
{
    internal const string DefaultMount = "/license";
    internal const string FileVariable = "INTELLIJ_SERVER_LICENSE_FILE";
    private const string Later = "2018.2 or later";
    private const string Earlier = "2018.1 or earlier";
    private static readonly string[] ProductCodes = ["ILS", "II"];

    /// <summary>The release date of the pinned server build (product-info.json majorVersionReleaseDate), for a perpetual fallback licence.</summary>
    private static readonly DateOnly ServerRelease = new(2026, 10, 14);

    private const int MaxFiles = 64;
    private const int MaxEntries = 16;
    private const int MaxCodeBytes = 64 * 1024;
    private const int MaxZipBytes = 1024 * 1024;

    /// <summary>The outcome of the last launch's look, or <see langword="null"/> when nothing was mounted or named.</summary>
    internal static MountedLicense? Current { get; set; }

    private sealed record Code(string Id, string Text, JsonArray Products, DateOnly? PaidUpTo, DateOnly? Fallback);

    /// <summary>
    /// Looks for a mounted or named licence and, when it is usable, writes it into <paramref name="sessionDirectory"/>. Returns the launch environment changes:
    /// only the path of the written file, or the removal of a name that cannot work. <paramref name="environment"/>, <paramref name="mount"/> and
    /// <paramref name="today"/> are the process environment, the mount directory and the clock.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> Prepare(string sessionDirectory, Func<string, string?>? environment = null, string mount = DefaultMount, DateOnly? today = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var none = new Dictionary<string, string?>();
        var named = environment(FileVariable) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        var source = named ?? (Directory.Exists(mount) || File.Exists(mount) ? mount : null);
        if (source is null) { Current = null; return none; }
        if (!string.IsNullOrWhiteSpace(environment("INTELLIJ_SERVER_LICENSE")))
        {
            Current = new("overridden", null, "INTELLIJ_SERVER_LICENSE is set and wins over the mounted licence, which is ignored.", false);
            return none;
        }
        if (!string.IsNullOrWhiteSpace(environment("JETBRAINS_LICENSE_SERVER")))
        {
            Current = new("overridden", null, "JETBRAINS_LICENSE_SERVER is set and wins over the mounted licence, which is ignored.", false);
            return none;
        }
        var outcome = Resolve(source, today ?? DateOnly.FromDateTime(DateTime.UtcNow));
        if (outcome.Code is null)
        {
            // A bare file the user named that merely looks unfamiliar is left to the server, which knows more formats than this check.
            if (named is not null && outcome.Kind == "invalid" && File.Exists(named) && !named.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Current = new("unchecked", null, "The file named by INTELLIJ_SERVER_LICENSE_FILE does not look like an offline activation code; the server decides whether it accepts it.", false);
                return none;
            }
            Current = new(outcome.Kind, null, outcome.Message, false);
            return named is not null ? new Dictionary<string, string?> { [FileVariable] = null } : none;
        }
        string path;
        try { path = Write(sessionDirectory, outcome.Code.Text); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Current = new("unreadable", outcome.Code.Id, $"Licence {outcome.Code.Id} was found but could not be written for the server (the cache is not writable).", false);
            return none;
        }
        Current = new(outcome.Kind, outcome.Code.Id, outcome.Message, true);
        return new Dictionary<string, string?> { [FileVariable] = path };
    }

    private static string Write(string sessionDirectory, string code)
    {
        Directory.CreateDirectory(sessionDirectory);
        var path = Path.Combine(sessionDirectory, "license.key");
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options)) stream.Write(Encoding.ASCII.GetBytes(code));
        return path;
    }

    private sealed record Outcome(Code? Code, string Kind, string Message);

    private static Outcome Bad(string kind, string message) => new(null, kind, message);

    private static Outcome Resolve(string source, DateOnly today)
    {
        var codes = new List<Code>();
        var problems = new List<string>();
        void Offer(string? text, string where)
        {
            if (Parse(text, out var code, out var problem)) codes.Add(code!);
            else problems.Add($"{where}: {problem}");
        }
        try
        {
            if (Directory.Exists(source))
            {
                var files = Directory.EnumerateFiles(source).Where(file => !Path.GetFileName(file).StartsWith('.')).Order(StringComparer.Ordinal).ToList();
                if (files.Count > MaxFiles) return Bad("invalid", $"The licence directory holds {files.Count} files; keep only the downloaded zip or its text files (at most {MaxFiles}).");
                foreach (var file in files) Collect(file, Offer);
                if (files.Count == 0) return Bad("invalid", "The licence directory is empty. Put the zip downloaded from account.jetbrains.com (or the \"for 2018.2 or later\" text file from it) there.");
            }
            else Collect(source, Offer);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Bad("unreadable", "The licence directory or file could not be read (check the mount and that the container user can read it).");
        }
        if (codes.Count == 0)
            return Bad("invalid", problems.Count == 0 ? "No offline activation code was found in the licence directory." : "No usable offline activation code: " + string.Join("; ", problems.Distinct().Take(3)) + ".");
        var distinct = codes.GroupBy(code => code.Id, StringComparer.Ordinal).Select(group => group.First()).ToList();
        Code chosen;
        if (distinct.Count == 1) chosen = distinct[0];
        else
        {
            var matching = distinct.Where(code => code.Products.Any(product => product?["code"]?.GetValue<string>() is { } id && ProductCodes.Contains(id))).ToList();
            if (matching.Count != 1)
                return Bad("ambiguous", $"The licence directory holds {distinct.Count} licences ({string.Join(", ", distinct.Select(code => code.Id).Order(StringComparer.Ordinal))}) and {(matching.Count == 0 ? "none" : "more than one")} is for IntelliJ IDEA; mount a directory with only the one you want.");
            chosen = matching[0];
        }
        if (Expired(chosen, today))
            return new(chosen, "expired", $"Licence {chosen.Id} looks expired (paid up to {chosen.PaidUpTo:yyyy-MM-dd}); it is handed to the server anyway, whose own verdict decides.");
        return new(chosen, "provided", $"Licence {chosen.Id} was handed to the server; its own state shows whether it accepted it.");
    }

    /// <summary>Reads a zip (its entry for 2018.2 or later) or a text file (not the one for 2018.1 or earlier) and offers each code found.</summary>
    private static void Collect(string file, Action<string?, string> offer)
    {
        var name = Path.GetFileName(file);
        if (name.Contains(Earlier, StringComparison.OrdinalIgnoreCase)) return;
        var info = new FileInfo(file);
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            if (info.Length > MaxZipBytes) { offer(null, "a .zip: it is larger than 1 MiB"); return; }
            try
            {
                using var archive = ZipFile.OpenRead(file);
                if (archive.Entries.Count > MaxEntries) { offer(null, "a .zip: it holds too many entries"); return; }
                var wanted = archive.Entries.Where(entry => !entry.FullName.EndsWith('/') && !entry.Name.Contains(Earlier, StringComparison.OrdinalIgnoreCase)).ToList();
                var later = wanted.Where(entry => entry.Name.Contains(Later, StringComparison.OrdinalIgnoreCase)).ToList();
                if (later.Count == 0) { offer(null, "a .zip: it has no entry \"for " + Later + "\""); return; }
                foreach (var entry in later)
                {
                    if (entry.Length > MaxCodeBytes) { offer(null, "a .zip entry is too large"); continue; }
                    using var input = entry.Open();
                    offer(ReadText(input), "a .zip entry");
                }
            }
            catch (InvalidDataException) { offer(null, "a .zip: it cannot be opened as a zip"); }
            return;
        }
        if (info.Length > MaxCodeBytes) { offer(null, "a file: it is too large to be a code"); return; }
        using var stream = File.OpenRead(file);
        offer(ReadText(stream), "a file");
    }

    private static string ReadText(Stream input)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    /// <summary>Checks the shape <c>ID-base64json-base64-base64</c> where the JSON starts with a licenseId. Never echoes the text.</summary>
    private static bool Parse(string? text, out Code? code, out string problem)
    {
        code = null;
        problem = "";
        var clean = text is null ? "" : string.Concat(text.Where(character => !char.IsWhiteSpace(character) && character != '﻿'));
        var parts = clean.Split('-');
        if (clean.Length == 0) { problem = "it is empty"; return false; }
        if (parts.Length != 4 || parts[0].Length is < 6 or > 20 || !parts[0].All(char.IsAsciiLetterOrDigit) || parts.Skip(1).Any(part => part.Length == 0 || !part.All(IsBase64)))
        {
            problem = "it does not have the shape of an offline activation code (ID, then three base64 parts joined by -)";
            return false;
        }
        JsonObject? json;
        try { json = JsonNode.Parse(Decode(parts[1])) as JsonObject; }
        catch (Exception error) when (error is FormatException or JsonException or DecoderFallbackException) { json = null; }
        if (json?["licenseId"] is not JsonValue licence || !licence.TryGetValue<string>(out var id) || id.Length == 0)
        {
            problem = "its first base64 part is not a licence description";
            return false;
        }
        var products = json["products"] as JsonArray ?? [];
        code = new(parts[0], clean, products, Latest(products, "paidUpTo"), Latest(products, "fallbackDate"));
        return true;
    }

    private static bool IsBase64(char character) => char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '=';

    private static string Decode(string base64)
    {
        var padded = base64.TrimEnd('=');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static DateOnly? Latest(JsonArray products, string field)
    {
        DateOnly? latest = null;
        foreach (var product in products)
            if (product?[field] is JsonValue value && value.TryGetValue<string>(out var text) && DateOnly.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var date) && (latest is null || date > latest))
                latest = date;
        return latest;
    }

    /// <summary>Expired only when the licence says so plainly: a paid-up date before today and no fallback date reaching the server build.</summary>
    private static bool Expired(Code code, DateOnly today) =>
        code.PaidUpTo is { } paid && paid < today && (code.Fallback is not { } fallback || fallback < ServerRelease);
}