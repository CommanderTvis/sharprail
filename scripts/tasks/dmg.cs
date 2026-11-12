#!/usr/bin/env dotnet
// `scripts/mise run dmg [path]`: packs artifacts/tereditor.app (from package:macos) into a disk image with the usual Applications shortcut.
// The path argument chooses the image; the default is artifacts/tereditor.dmg.
#:include ../lib/Tasks.cs
using static Tasks;

return Guard(() =>
{
    var bundle = "artifacts/tereditor.app";
    var image = args.Length > 0 && args[0].Length > 0 ? args[0] : "artifacts/tereditor.dmg";
    if (!Directory.Exists(bundle)) throw new TaskFailure($"No {bundle}; run scripts/mise run package:macos first.");
    var staging = Directory.CreateTempSubdirectory().FullName;
    try
    {
        // ditto keeps the bundle's symlinks and signature intact.
        Check("ditto", [bundle, Path.Combine(staging, "tereditor.app")]);
        File.CreateSymbolicLink(Path.Combine(staging, "Applications"), "/Applications");
        DeleteAll(image);
        Check("hdiutil", ["create", "-volname", "tereditor", "-srcfolder", staging, "-fs", "HFS+", "-format", "UDZO", "-quiet", image]);
    }
    finally { DeleteAll(staging); }
    Console.WriteLine(image);
    return 0;
});
