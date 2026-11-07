[简体中文](README.md) · **English** · [繁體中文](README.zh-TW.md)

# dsh-everything

[Everything](https://www.voidtools.com/) filename search for [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness),
packaged as a DSH bundle.

**Windows only.** Everything itself is a Windows application, and the IPC bridge is a small .NET binary.

## What it does

Registers five MCP tools (`mcp__everything__*`) through `@deepseek-ai/dsh-mcp-client`. Every query goes to a
prebuilt index that already exists on the machine, so lookups return in milliseconds without walking directories:

| Tool | Purpose |
|---|---|
| `everything_search` | Full Everything query syntax: name, path, size, date, wildcards, regex, `dupe:`, `empty:`, … |
| `everything_count` | Count matches without transferring rows |
| `everything_status` | Is Everything running / index loaded / elevated |
| `everything_recent` | Recently modified items (`dm:lastNdays`) |
| `everything_duplicates` | Names that occur more than once |

```
DSH --(MCP stdio)-->  mcp/everything-mcp.mjs  --(line JSON)-->  mcp/everything-ipc.exe  --(WM_COPYDATA)-->  Everything.exe
```

`everything-ipc.exe` is a zero-dependency C# bridge that speaks Everything's own IPC protocol. Everything's
command line has no "search and print results" switch, which is why a bridge is needed at all. Sources ship in
`mcp/everything-ipc.cs`; rebuild with the .NET Framework compiler that ships with Windows:

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:anycpu /optimize+ `
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
  '/out:everything-ipc.exe' 'everything-ipc.cs'
```

> `csc.exe` mishandles forward-slash paths: `mcp/everything-ipc.cs` resolves to `.\everything-ipc.cs`
> (CS1504). Use backslashes.

## Requirements

1. Windows 10/11.
2. Everything installed and **running**. The index belongs to Everything; this bundle only queries it.
   Recommended setup — the service indexes NTFS volumes, the GUI does not need elevation:
   ```powershell
   & 'C:\Program Files (x86)\Everything\Everything.exe' -install-service          # admin, once
   & 'C:\Program Files (x86)\Everything\Everything.exe' -install-run-on-system-startup
   ```
   Then untick **Run as administrator** in Everything's options, and keep
   `ntfs_volume_load_recent_changes=1,1,1` in `%APPDATA%\Everything\Everything.ini` — with `0,0,0` Everything
   never replays the USN journal, so changes made while it was closed are missing from the index forever.
3. DSH `0.2.2-rc.1` or a later `0.2.x`.

## Install

```text
plugin_manager install_bundle   target=<npm spec> | github:<owner>/dsh-everything | link:<local path>
```

Installing is all that is needed: the bundle's patch inserts the MCP client row, computes the absolute path to
its own server inside the profile, and registers the tools. No manual configuration, no restart (the loader
hot-applies new entries).

The plugin also declares `dsh.bundle.platforms: ["win32"]`, so on Linux or macOS the whole bundle is skipped by
the host instead of failing at spawn time.

## How the two tricky bits work

**Self-referencing path.** A patch is static YAML but `args` needs an absolute path. The Loader evaluates
`!!js` expressions against the profile directory (`ctx.baseUrl`), so the row can point at
`node_modules/dsh-everything/mcp/everything-mcp.mjs` without hard-coding any user path. Two traps:
a `: ` (colon-space) inside the expression makes YAML parse it as a mapping, and `pathname` yields `/C:/…`
on Windows (the expression strips that leading slash).

**No dependency on `node` being on PATH.** `command` is `process.execPath` — DeepSeek Harness itself, which is
Electron — with `env.ELECTRON_RUN_AS_NODE=1`. The host binary then behaves as a Node runtime (measured
`v24.21.0`), so the plugin works on machines without a standalone Node install.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `Everything is not running` | Start Everything; it owns the index. |
| `serverName "everything" is already in use` | Another mcp-client row already uses the name — remove or rename the hand-written row. |
| Tools missing entirely | Check `everything_status`; also confirm the profile lists `dsh-everything` in `dsh.profile.bundles`. |
| Empty results for a file you just created | `ntfs_volume_load_recent_changes` or a stale index; run `Everything.exe -reindex`. |

## Query gotchas

- Date offsets need a prefix: `dm:last7days` is the last seven days, while `dm:7days` silently matches
  unrelated old files.
- A leading backslash matches the full path: `\node_modules` hits that whole subtree, `node_modules` only hits
  entries actually named that.
- Type macros `exe:` / `doc:` / `pic:` are user-defined filters; undefined ones return zero results silently.
  Use `ext:exe` instead.

## License

MIT. Not affiliated with or endorsed by voidtools.
