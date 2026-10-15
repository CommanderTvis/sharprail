# Core icon sources

The SVG glyphs use the original 24×24 paths from [`@remixicon/react` 4.9.0](https://www.npmjs.com/package/@remixicon/react/v/4.9.0), under [Remix Icon License 1.0](../../../../licenses/RemixIcon.txt). `sources.json` maps SharpRail's existing aliases to the package's `Ri`-prefixed exports. The package metadata pins 4.9.0; its bundled JavaScript header still says 4.1.0.

`fileDiff.svg` is the original `apps/web/public/custom-icons/file-diff-line.svg` from [ThinkRail at `4ab88b5228dde1f234c54cf70319e58890acf650`](https://github.com/CommanderTvis/thinkrail/blob/4ab88b5228dde1f234c54cf70319e58890acf650/apps/web/public/custom-icons/file-diff-line.svg), copied byte-for-byte, under the project's Apache-2.0 license. `brand.png` remains the prototype's raster brand mark because its original SVG source is unavailable.

These monochrome SVGs contain only filled paths in a 24×24 viewbox. `Ui.Icon` retains that full box and fills the parsed paths with a vector opacity mask, so its `Border.Background` controls tint at the final render scale. New assets must preserve this format or extend the parser explicitly; no bitmap conversion is used.
