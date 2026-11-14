# Core icon sources

The SVG glyphs use the original 24×24 paths from [`@remixicon/react` 4.9.0](https://www.npmjs.com/package/@remixicon/react/v/4.9.0), under [Remix Icon License 1.0](../../../../licenses/RemixIcon.txt). `sources.json` maps tereditor's existing aliases to the package's `Ri`-prefixed exports. The package metadata pins 4.9.0; its bundled JavaScript header still says 4.1.0.

`fileDiff.svg` is the original `apps/web/public/custom-icons/file-diff-line.svg` from ThinkRail at `4ab88b522`, copied byte-for-byte, under the project's Apache-2.0 license. `brand.png` is the tereditor app icon (the frog), a raster image shown in its own colours.

These monochrome SVGs contain only filled paths in a 24×24 viewbox. `Ui.Icon` retains that full box and fills the parsed paths with a vector opacity mask, so its `Border.Background` controls tint at the final render scale. New assets must preserve this format or extend the parser explicitly; no bitmap conversion is used.

`radioTower.svg`, `plug.svg` and `unplug.svg` are the Host block's state marks: Lucide's `radio-tower`, `plug` and `unplug` from [`lucide-static` 0.460.0](https://unpkg.com/lucide-static@0.460.0/icons/), under the [ISC License](../../../../licenses/Lucide-ISC.txt). Lucide draws 2px round strokes, so each was turned into filled outlines with `uvx picosvg <icon>.svg` (strokes and circles become nonzero-filled paths) and only its `<path d>` values were kept in this file format. They are not Remix glyphs, so `sources.json` has no entry for them.

`chrome.svg` is the `googlechrome` mark from [Simple Icons](https://simpleicons.org/) (CC0 1.0), one 24×24 path, used by the Claude Code terminal's Chrome chip. It is not a Remix glyph, so `sources.json` has no entry for it.
