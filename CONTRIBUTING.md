# Contributing

Issues and pull requests are welcome — in English or Chinese.

## Layout

```
cordis.patch.yml      bundle layer: inserts the mcp-client row (this is the whole "plugin")
mcp/everything-mcp.mjs  MCP stdio server: 5 tools, zero npm dependencies
mcp/everything-ipc.cs   zero-dependency C# bridge speaking Everything's WM_COPYDATA IPC
mcp/everything-ipc.exe  prebuilt bridge (CI rebuilds it on every push)
locale/*.json           plugin page title/description (en, zh)
assets/hero.png         README hero image (real Everything screenshot + composed panel)
scripts/smoke.mjs       protocol self-test over stdio; does not need Everything running
```

## Local loop

```powershell
# protocol self-test (initialize + tools/list + status)
node scripts\smoke.mjs

# server status against the running Everything
node mcp\everything-mcp.mjs status

# install this working copy into a profile (hot-applies)
plugin_manager install_bundle   target=link:D:\dsh\projects\dsh-everything
```

After editing `cordis.patch.yml`, re-run `set_bundle` with the same target to hot-apply it.
A `serverName` stays reserved until its instance is disposed, so removing a row and re-adding it
requires a host restart; renaming it does not.

## Rebuilding the bridge

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:anycpu /optimize+ `
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
  '/out:mcp\everything-ipc.exe' 'mcp\everything-ipc.cs'
```

`csc.exe` mishandles forward slashes (`mcp/x.cs` resolves to `.\x.cs`, CS1504) — use backslashes.

## CI

`.github/workflows/build.yml` runs on `windows-latest`: compile the bridge → print its SHA-256 →
`node --check` → `node scripts/smoke.mjs` → upload the artifact.

## Regenerating the hero image

`assets/hero.png` is composed from `assets/everything-window.png`, a real screen capture of Everything
(`PrintWindow`, so the window can stay behind others). Rebuild the composition with Pillow if the
screenshot or the copy changes; keep the canvas at 1200 px wide so GitHub renders it crisply.
