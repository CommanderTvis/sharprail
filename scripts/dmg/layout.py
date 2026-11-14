# /// script
# requires-python = "==3.13.*"
# dependencies = ["dmgbuild==1.6.7", "ds_store==1.3.3", "mac_alias==2.2.3"]
# ///
# Regenerates scripts/dmg/DS_Store, the disk image window's layout that `scripts/mise run dmg` copies into the image:
# `uv run --script scripts/dmg/layout.py` (macOS; uv from anywhere, it is not pinned). Rerun it after changing
# settings.py; a new background image alone needs no rerun, since the layout only names the file.
# It builds a throwaway image with dmgbuild from settings.py and a stub tereditor.app, attaches it read-only without
# showing it, copies the .DS_Store out and detaches. The background alias in it then gets the paths the image has
# when mounted normally (/Volumes/tereditor), whatever this machine had mounted.
import shutil
import subprocess
import tempfile
from pathlib import Path

import dmgbuild
from ds_store import DSStore
from mac_alias import Alias

HERE = Path(__file__).resolve().parent
ICON = HERE.parent.parent / "src/Tereditor.UI/AppIcon/compiled/tereditor.icns"
VOLUME = "tereditor"

with tempfile.TemporaryDirectory() as temp:
    temp = Path(temp)
    resources = temp / "tereditor.app/Contents/Resources"
    resources.mkdir(parents=True)
    shutil.copyfile(ICON, resources / "tereditor.icns")
    image, mount = temp / "layout.dmg", temp / "mount"
    dmgbuild.build_dmg(str(image), VOLUME, settings_file=str(HERE / "settings.py"),
                       defines={"app": str(temp / "tereditor.app"), "background": str(HERE / "background.png")})
    mount.mkdir()
    subprocess.run(["hdiutil", "attach", "-nobrowse", "-readonly", "-noautoopen", "-quiet", "-mountpoint", str(mount), str(image)], check=True)
    try:
        shutil.copyfile(mount / ".DS_Store", HERE / "DS_Store")
    finally:
        subprocess.run(["hdiutil", "detach", "-quiet", str(mount)], check=True)

with DSStore.open(str(HERE / "DS_Store"), "r+") as store:
    view = dict(store["."]["icvp"])
    alias = Alias.from_bytes(view["backgroundImageAlias"])
    alias.volume.posix_path = f"/Volumes/{VOLUME}"
    alias.target.folder_name = VOLUME
    view["backgroundImageAlias"] = alias.to_bytes()
    store["."]["icvp"] = view
print(HERE / "DS_Store")
