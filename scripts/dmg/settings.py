# dmgbuild settings from which scripts/dmg/layout.py regenerates scripts/dmg/DS_Store, the layout that
# scripts/tasks/dmg.cs copies into the disk image; the build itself does not read this file. layout.py passes a stub
# bundle and the background image as `-D app=...` and `-D background=...`. They describe the Finder window
# the image opens in: 540x380, the app on the left and Applications on the right over background.png (with its @2x),
# 128 pt icons, no toolbar, sidebar, path bar or status bar. Rerun layout.py after changing them.
import os.path

app = defines["app"]

format = "UDZO"
filesystem = "HFS+"
files = [app]
symlinks = {"Applications": "/Applications"}
icon = os.path.join(app, "Contents", "Resources", "tereditor.icns")

background = defines["background"]
window_rect = ((200, 160), (540, 380))
default_view = "icon-view"
show_toolbar = False
show_sidebar = False
show_pathbar = False
show_status_bar = False
show_tab_view = False
show_icon_preview = False
include_icon_view_settings = True
include_list_view_settings = False
arrange_by = None
icon_size = 128
text_size = 13
# Icon centres; background.py draws the arrow between them.
icon_locations = {os.path.basename(app): (140, 170), "Applications": (400, 170)}
