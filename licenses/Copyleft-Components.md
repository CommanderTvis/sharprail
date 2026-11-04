# Components under weak-copyleft licenses

None of these affects SharpRail's own code. They are unmodified, separately identifiable components.

## libfuse (LGPL-2.1), in the Linux AppImage

The AppImage starts through the AppImage type 2 runtime 20251108
(https://github.com/AppImage/type2-runtime, MIT), which statically links libfuse (LGPL-2.1,
https://github.com/libfuse/libfuse), squashfuse (BSD-2-Clause), zstd, zlib and musl. The LGPL-2.1 text is
in `LGPL-2.1.txt`. The runtime is unmodified; its source is at the URL above. The image is a SquashFS
archive that `./SharpRail-*.AppImage --appimage-extract` unpacks, and it can be repacked with a different
runtime, which satisfies the LGPL's replace-the-library requirement.

## Rust crates under MPL-2.0, inside Merman

`cssparser` 0.36.0, `dtoa-short` 0.3.5 and `selectors` 0.37.0 are linked, unmodified, into Merman's
`merman-ffi` (libSharpRailMermaid on macOS, Android and the browser). The MPL-2.0 text is in `MPL-2.0.txt`.
Source: https://crates.io/crates/cssparser, https://crates.io/crates/dtoa-short and
https://crates.io/crates/selectors; the versions are listed in `Merman-Crates.txt`.

## FreeType (FreeType License or GPL-2.0), inside the Ghostty libraries

FreeType is dual-licensed under the FreeType License (FTL) and GPL-2.0. SharpRail uses it under the FTL.
The text is in `FreeType-FTL.txt`. This product includes software developed by the FreeType Project
(https://freetype.org). Portions of this software are copyright The FreeType Project (www.freetype.org).
All rights reserved.
