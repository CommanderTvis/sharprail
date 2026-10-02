# File Icons

Ports fork `packages/plugin-file-icons` at `40678fd30`. UI-only enabled builtin; no host module.
The file icon slot resolves lowercase whole filenames before extensions, longest extension first.
Unknown files use the material file icon; directories return null and retain the core folder glyph.
The committed table contains 2,127 filenames and 1,348 extensions.

## Build and assets

Material Icon Theme is pinned to 5.38.1 with a checksummed npm archive. The owning C# generator
recolours fills, strokes and stops to currentColor, preserves pale accents at .45 opacity, flattens
gradients and removes definitions, matching all 1,251 fork SVGs byte for byte. Generated SVGs live
in ignored `.tools/file-icons/assets/file-icons`, never source control. The contract build copies
them into `plugins/file-icons/assets/file-icons` for host asset serving and publishing.

Run `.tools/dotnet/dotnet run src/SharpRail.Plugins.FileIcons/scripts/generate-file-icons.cs -- --check`
to verify table and assets. Default mode regenerates both; --assets-only refuses a stale table.
The build script serializes generation and caches its source fingerprint.

## Rendering and boundary

The plugin references only API/UI contracts. Each file yields a stable asset identifier; core caches
asset bytes and retains icon controls during resize. The shared SVG primitive responds to theme
changes. Missing or malformed assets retain the core plain file glyph. No folder icon set is ported.

## Verification

`--file-icons` covers lookup precedence, output asset serving and traversal protection, local/gRPC
tree/tab/Changes glyphs, rendered theme changes, resize retention and disable/re-enable. Missing and
malformed SVGs preserve fallback. Generated assets and table match fork; contract publishing includes
all assets. Native appearance, canonical app publish and full-suite gates remain open.
