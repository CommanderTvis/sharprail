using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SharpRail.Checks;

/// <summary>
/// The built Android package (scripts/mise run package:android): Merman ships for every packaged ABI under the name the
/// renderer's P/Invoke loads, for that ABI's machine, with the C ABI the desktop uses and 16 KB aligned segments.
/// </summary>
internal static class AndroidPackageChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run(string? package)
    {
        package ??= Path.Combine(FindRepository(), "artifacts", "android", "SharpRail.apk");
        Require(File.Exists(package), $"{package} does not exist; run scripts/mise run package:android first.");
        using var archive = ZipFile.OpenRead(package);
        foreach (var (abi, machine) in new[] { ("arm64-v8a", (ushort)0xB7), ("x86_64", (ushort)0x3E) })
        {
            var entry = archive.GetEntry($"lib/{abi}/libSharpRailMermaid.so");
            Require(entry is not null, $"The package has no Merman library for {abi}.");
            using var stream = new MemoryStream();
            using (var input = entry!.Open()) input.CopyTo(stream);
            var elf = stream.ToArray();
            Require(elf.Length > 64 && elf.AsSpan(0, 4).SequenceEqual("\u007fELF"u8) && elf[4] == 2, $"The {abi} Merman library is not a 64-bit ELF file.");
            Require(BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(0x12)) == machine, $"The {abi} Merman library is built for another machine.");
            var headers = (int)BinaryPrimitives.ReadUInt64LittleEndian(elf.AsSpan(0x20));
            var size = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(0x36));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(0x38));
            var loads = 0;
            for (var index = 0; index < count; index++)
            {
                var header = elf.AsSpan(headers + index * size);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 1) continue;
                loads++;
                Require(BinaryPrimitives.ReadUInt64LittleEndian(header[0x30..]) >= 0x4000, $"The {abi} Merman library has a segment aligned below 16 KB.");
            }
            Require(loads > 0, $"The {abi} Merman library has no loadable segment.");
            foreach (var symbol in new[] { "merman_render_svg", "merman_buffer_free" })
                Require(elf.AsSpan().IndexOf(Encoding.ASCII.GetBytes(symbol + "\0")) >= 0, $"The {abi} Merman library does not name {symbol}.");
        }
        Console.WriteLine("Android package checks passed.");
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SharpRail.slnx"))) return directory.FullName;
        throw new InvalidOperationException("The checks are not running from a SharpRail checkout.");
    }
}