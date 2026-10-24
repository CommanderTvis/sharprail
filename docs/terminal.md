# Terminal rendering

## How it draws

- The workbench renders through Skia on the GPU, including a custom Scintilla surface for the editor.
- On macOS, Ghostty's Metal terminal textures are composed directly into that view, without copying frames back to the
  CPU. Skia is the fallback.
- Mermaid diagrams use Merman, a native Rust renderer, and display as vector SVGs without a JavaScript engine.
- Published .NET 10 builds start with precompiled code and tune hot paths as you work, while keeping support for loading
  managed plugins.

## Comparison with Ghostty

Single runs on the same MacBook Pro (October 11, 2026), with a 48 × 93 grid and `term-bench` 0.10.15-alpha, using
`--duration 15 --fps 120`.

| Metric | SharpRail Metal texture | Ghostty |
| --- | ---: | ---: |
| Generated FPS | 108.7 | 109.4 |
| Average draw call | 1.91 ms | 1.22 ms |
| Worst draw call | 56.53 ms | 7.81 ms |

What it shows:

- Both looked smooth, with similar paced output rates.
- Ghostty had shorter draw calls.
- The benchmark measures generation and output, including PTY backpressure. It does not measure GPU time or frames
  actually displayed.

Renderer benchmark sources and reproduction commands are in [`benchmarks/ghostty`](../benchmarks/ghostty/README.md).
The reusable terminal controls live in `src/Ghostty.Avalonia`, and its README documents the API, build requirements and
limits of the Skia path.
