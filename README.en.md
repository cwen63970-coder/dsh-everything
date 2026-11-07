<div align="center">

[简体中文](README.md) · **English** · [繁體中文](README.zh-TW.md)

# dsh-everything

### Everything's filename index, wired into DeepSeek Harness as a bundle you install and forget.

**Find any file in milliseconds — no directory walking, no remembering which drive it lives on.**

[![build](https://github.com/cwen63970-coder/dsh-everything/actions/workflows/build.yml/badge.svg)](https://github.com/cwen63970-coder/dsh-everything/actions/workflows/build.yml)
[![release](https://img.shields.io/badge/release-v0.1.0-2f6feb?style=flat-square)](https://github.com/cwen63970-coder/dsh-everything/releases)
[![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=flat-square&logo=windows&logoColor=white)](#requirements)
[![DSH](https://img.shields.io/badge/DSH-%E2%89%A5%200.2.2--rc.1-4B32C3?style=flat-square)](#requirements)
[![MCP tools](https://img.shields.io/badge/MCP-5%20tools-orange?style=flat-square)](#what-it-does)
[![license](https://img.shields.io/badge/license-MIT-f4c430?style=flat-square)](LICENSE)

[Install](#install) · [What it does](#what-it-does) · [How it works](#how-it-works) · [Query gotchas](#query-gotchas) · [Troubleshooting](#troubleshooting)

<code>plugin_manager install_bundle&nbsp;&nbsp;target=github:cwen63970-coder/dsh-everything</code>

</div>

> [!IMPORTANT]
> **Windows only.** Everything itself is a Windows application, and the IPC bridge is a small .NET binary.
> Everything must be installed and **running** — the index belongs to it; this plugin only queries.

<div align="center">
<br />
<img src="assets/banner.svg" alt="dsh-everything" width="100%" />
<br />
</div>

---

Everything keeps an index of every filename on every local volume. This bundle hands that index to the agent:
a query comes back in milliseconds, without walking a directory tree and without any cloud service.

Installing adds five tools named `mcp__everything__*` — the same names a hand-written MCP configuration would
produce. No profile edits, no restart, no first-run choices.

## Requirements

1. Windows 10/11.
2. Everything installed and running. Recommended setup — the service indexes NTFS, the GUI stays unelevated:
   ```powershell
   & 'C:\Program Files (x86)\Everything\Everything.exe' -install-service          # admin, once
   & 'C:\Program Files (x86)\Everything\Everything.exe' -install-run-on-system-startup
   ```
   Then untick **Run as administrator** in Everything's options and keep
   `ntfs_volume_load_recent_changes=1,1,1` in `%APPDATA%\Everything\Everything.ini`
   (with `0,0,0` Everything never replays the USN journal, so changes made while it was closed are lost).
3. DSH `0.2.2-rc.1` or a later `0.2.x`.

## Install

```text
plugin_manager install_bundle   target=<npm spec> | github:<owner>/dsh-everything | link:<local path>
```

The bundle's patch inserts the MCP client row, computes the absolute path to its own server inside the profile,
and registers the tools. The loader hot-applies new entries, so it works immediately.

The manifest declares `dsh.bundle.platforms: ["win32"]`, so on Linux or macOS the host skips the whole bundle
instead of failing at spawn time.

## What it does

| Tool | Purpose |
|---|---|
| `everything_search` | Full Everything query syntax: name, path, size, date, wildcards, regex, `dupe:`, `empty:`, … |
| `everything_count` | Count matches without transferring rows |
| `everything_status` | Is Everything running / index loaded / elevated |
| `everything_recent` | Recently modified items (`dm:lastNdays`) |
| `everything_duplicates` | Names that occur more than once |

## How it works

```
DSH --(MCP stdio)-->  mcp/everything-mcp.mjs  --(line JSON)-->  mcp/everything-ipc.exe  --(WM_COPYDATA)-->  Everything.exe
```

Everything's command line has no "search and print results" switch, which is why the bridge exists: zero
dependencies, and it speaks Everything's own IPC protocol. Sources ship in `mcp/everything-ipc.cs`; rebuild with
the .NET Framework compiler that ships with Windows:

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:anycpu /optimize+ `
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
  '/out:everything-ipc.exe' 'everything-ipc.cs'
```

> [!NOTE]
> `csc.exe` mishandles forward-slash paths: `mcp/everything-ipc.cs` resolves to `.\everything-ipc.cs` (CS1504).
> Use backslashes.

**Self-referencing path.** A patch is static YAML but `args` needs an absolute path. The Loader evaluates
`!!js` expressions against the profile directory (`ctx.baseUrl`), so the row can point at
`node_modules/dsh-everything/mcp/everything-mcp.mjs` without hard-coding any user path. Two traps:
a `: ` (colon-space) inside the expression makes YAML parse it as a mapping, and `pathname` yields `/C:/…`
on Windows (the expression strips that leading slash).

**No dependency on `node` being on PATH.** `command` is `process.execPath` — DeepSeek Harness itself, which is
Electron — with `env.ELECTRON_RUN_AS_NODE=1`. The host binary then behaves as a Node runtime (measured
`v24.21.0`), so the plugin works on machines without a standalone Node install.

## Query gotchas

Only the three that bite most often; the full syntax lives in Everything's own help (Help → Search Syntax):

| Query | Meaning |
|---|---|
| `dm:last7days` | The last seven days. A bare `dm:7days` is **not** that — it silently matches unrelated old files |
| `\node_modules` | Matches the full path, so it hits that whole subtree; `node_modules` only hits entries named that |
| `ext:exe` | Filter by extension. Type macros `exe:` / `doc:` / `pic:` are user-defined filters; undefined ones return zero silently |

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `Everything is not running` | Start Everything; it owns the index |
| `serverName "everything" is already in use` | Another mcp-client row uses the name — remove or rename the hand-written row |
| Tools missing entirely | Check `everything_status`; confirm the profile lists `dsh-everything` in `dsh.profile.bundles` |
| Empty results for a file you just created | `ntfs_volume_load_recent_changes` or a stale index; run `Everything.exe -reindex` |

## Development

```powershell
# protocol smoke test (does not need Everything running)
node scripts\smoke.mjs

# local status
node mcp\everything-mcp.mjs status

# install your local checkout into a profile
plugin_manager install_bundle   target=link:D:\dsh\projects\dsh-everything
```

CI runs on `windows-latest`: `csc` compiles the bridge → prints its SHA-256 → `node --check` → `scripts/smoke.mjs`
→ uploads the artifact.

## License

MIT. Not affiliated with or endorsed by voidtools.
