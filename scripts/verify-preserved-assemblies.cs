// Fails unless every managed assembly in the browser build's manifest was published byte for byte.
// Run: dotnet run scripts/verify-preserved-assemblies.cs -- <manifest> (lines "original|published")
using System.Security.Cryptography;

if (args.Length < 1) { Console.Error.WriteLine("usage: dotnet run scripts/verify-preserved-assemblies.cs -- <manifest>"); return 2; }
var entries = File.ReadAllText(args[0]).Trim().Split(["\r\n", "\n"], StringSplitOptions.None).Where(line => line.Length > 0).ToArray();
if (entries.Length == 0) return Fail("The browser copy-only publication must preserve managed assemblies.");
foreach (var entry in entries)
{
    var parts = entry.Split('|');
    if (parts[0].Length == 0 || parts.Length < 2 || parts[1].Length == 0) return Fail("The preserved-assembly manifest must name both files.");
    if (!SHA256.HashData(File.ReadAllBytes(parts[1])).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(parts[0])))) return Fail("Browser publication changed managed IL: " + parts[0]);
}
Console.WriteLine("Verified byte-identical preservation of " + entries.Length + " browser managed assemblies.");
return 0;

static int Fail(string message) { Console.Error.WriteLine(message); return 1; }
