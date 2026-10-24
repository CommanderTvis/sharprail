# Checks

```sh
dotnet run --project tests/SharpRail.Checks -c Release
```

`-- --editor`, `-- --terminals`, `-- --ghostty-skia`, `-- --sync` and `-- --workspaces` run focused
subsets. `scripts/mise run check:terminal`, `-- --native-terminal` and `-- --native-texture` exercise real
Ghostty windows and open test windows. Set `SHARPRAIL_TEST_GIT_SOURCE` to a Thinkrail
clone to include the Git fixtures. See `src/SharpRail.Scintilla/README.md` for the
editor control's architecture and limits.

The full list of check modes is in [Agent reference](agent-reference.md).
