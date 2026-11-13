#!/usr/bin/env dotnet
// `scripts/mise run dmg [path]`: packs artifacts/tereditor.app (from package:macos) into a disk image with the usual Applications shortcut,
// laid out by dmgbuild (scripts/dmg/settings.py) over the committed background (scripts/dmg/background.py draws it).
// The path argument chooses the image; the default is artifacts/tereditor.dmg.
#:include ../lib/Tasks.cs
using static Tasks;

return Guard(() =>
{
    var bundle = "artifacts/tereditor.app";
    var image = args.Length > 0 && args[0].Length > 0 ? args[0] : "artifacts/tereditor.dmg";
    if (!Directory.Exists(bundle)) throw new TaskFailure($"No {bundle}; run scripts/mise run package:macos first.");
    DeleteAll(image);
    // dmgbuild copies the bundle with ditto (symlinks and signature intact), attaches the image without showing it and
    // writes the window's .DS_Store itself, so no Finder runs. uv (pinned in mise.toml) fetches dmgbuild and
    // its dependencies at these versions.
    Check("uvx", ["--python", "3.13", "--from", "dmgbuild==1.6.7", "--with", "ds_store==1.3.3", "--with", "mac_alias==2.2.3",
        "dmgbuild", "-s", "scripts/dmg/settings.py", "-D", $"app={bundle}", "-D", "background=scripts/dmg/background.png", "tereditor", image]);
    Console.WriteLine(image);
    return 0;
});
