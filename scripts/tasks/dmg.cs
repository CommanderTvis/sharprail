#!/usr/bin/env dotnet
// `scripts/mise run dmg [path]`: packs artifacts/tereditor.app (from package:macos) into a disk image with the usual Applications shortcut,
// over the committed background (scripts/dmg/background.py draws it) and the committed window layout (scripts/dmg/DS_Store,
// which scripts/dmg/layout.py regenerates from scripts/dmg/settings.py).
// The path argument chooses the image; the default is artifacts/tereditor.dmg.
#:include ../lib/Tasks.cs
using static Tasks;

return Guard(() =>
{
    var bundle = Path.GetFullPath("artifacts/tereditor.app");
    var image = args.Length > 0 && args[0].Length > 0 ? args[0] : "artifacts/tereditor.dmg";
    if (!Directory.Exists(bundle)) throw new TaskFailure($"No {bundle}; run scripts/mise run package:macos first.");
    DeleteAll(image);
    // The steps and their order are dmgbuild's, which made the layout: its background alias records the background's
    // file ID, which matches when the files are added in the same order. Only hdiutil and system tools run, and the
    // image is attached without showing it, so no Finder runs.
    var temp = Directory.CreateTempSubdirectory("tereditor-dmg-").FullName;
    try
    {
        var writable = Path.Combine(temp, "tereditor-rw.dmg");
        var mount = Path.Combine(temp, "mount");
        Directory.CreateDirectory(mount);
        var (du, size) = Capture("du", ["-sk", bundle]);
        if (du != 0) throw new TaskFailure($"du -sk {bundle} failed with exit code {du}", du);
        // 128 MB of headroom for the background, volume icon and .DS_Store, as dmgbuild allows; resize trims it below.
        var kilobytes = 128 * 1024 + long.Parse(size.Split('\t')[0]);
        Check("hdiutil", ["create", "-ov", "-volname", "tereditor", "-fs", "HFS+", "-fsargs", "-c c=64,a=16,e=16", "-size", $"{kilobytes}k", writable]);
        Check("hdiutil", ["attach", "-nobrowse", "-noautoopen", "-owners", "off", "-mountpoint", mount, writable]);
        var attached = true;
        try
        {
            File.Copy(Path.Combine(bundle, "Contents", "Resources", "tereditor.icns"), Path.Combine(mount, ".VolumeIcon.icns"));
            // The volume's custom-icon Finder flag (kHasCustomIcon, 0x0400 at byte 8 of FinderInfo), as SetFile -a C sets it.
            Check("xattr", ["-wx", "com.apple.FinderInfo", new string('0', 16) + "0400" + new string('0', 44), mount]);
            // Both resolutions in one TIFF, byte for byte what dmgbuild makes from the two PNGs.
            Check("tiffutil", ["-cathidpicheck", At("scripts", "dmg", "background.png"), At("scripts", "dmg", "background@2x.png"),
                "-out", Path.Combine(mount, ".background.tiff")]);
            // ditto keeps the bundle's symlinks and signature.
            Check("ditto", [bundle, Path.Combine(mount, "tereditor.app")]);
            File.CreateSymbolicLink(Path.Combine(mount, "Applications"), "/Applications");
            File.Copy(At("scripts", "dmg", "DS_Store"), Path.Combine(mount, ".DS_Store"));
            DeleteAll(Path.Combine(mount, ".Trashes"));
            Check("sync", []);
            // A just-written volume can be busy for a moment; dmgbuild retries too.
            for (var attempt = 1; attached; attempt++)
            {
                if (Run("hdiutil", ["detach", "-quiet", mount]) == 0) attached = false;
                else if (attempt == 6) throw new TaskFailure($"hdiutil detach {mount} failed");
                else Thread.Sleep(attempt * 1000);
            }
        }
        finally
        {
            if (attached) Run("hdiutil", ["detach", "-force", "-quiet", mount]);
        }
        Check("hdiutil", ["resize", "-quiet", "-sectors", "min", writable]);
        Check("hdiutil", ["convert", writable, "-format", "UDZO", "-ov", "-o", image]);
    }
    finally
    {
        DeleteAll(temp);
    }
    Console.WriteLine(image);
    return 0;
});
