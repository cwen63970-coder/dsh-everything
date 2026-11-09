<div align="center">

[简体中文](README.md) · **English** · [繁體中文](README.zh-TW.md)

# dsh-everything-mcp

### Everything's filename index, exposed to DeepSeek Harness as a read-only lookup plugin.

**Locate any indexed file or folder in one query — paths and metadata only, no content reads, no writes.**

[![build](https://github.com/cwen63970-coder/dsh-everything-mcp/actions/workflows/build.yml/badge.svg)](https://github.com/cwen63970-coder/dsh-everything-mcp/actions/workflows/build.yml)
[![release](https://img.shields.io/badge/release-v0.1.0-2f6feb?style=flat-square)](https://github.com/cwen63970-coder/dsh-everything-mcp/releases)
[![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=flat-square&logo=windows&logoColor=white)](#requirements)
[![DSH](https://img.shields.io/badge/DSH-%E2%89%A5%200.2.2--rc.1-4B32C3?style=flat-square)](#requirements)
[![MCP tools](https://img.shields.io/badge/MCP-5%20tools-orange?style=flat-square)](#what-it-does)
[![license](https://img.shields.io/badge/license-MIT-f4c430?style=flat-square)](LICENSE)

[Install](#install) · [Requirements](#requirements) · [What it does](#what-it-does) · [Limits](#limits) · [How it works](#how-it-works) · [Query gotchas](#query-gotchas) · [Troubleshooting](#troubleshooting)

<code>Plugins → Install a third-party plugin: https://github.com/cwen63970-coder/dsh-everything-mcp</code>

</div>

> [!IMPORTANT]
> **Windows only.** Everything itself is a Windows application, and the IPC bridge is a small .NET binary.
> Everything must be installed and **running** — the index belongs to it; this plugin only queries.

<div align="center">
<br />
<img src="assets/hero.png" alt="dsh-everything-mcp: Everything search results and the agent's tool call" width="100%" />
<br />
</div>

---

## Install

In DSH open **Plugins → Install a third-party plugin**, then put the repository URL into **Add plugin**:

```text
https://github.com/cwen63970-coder/dsh-everything-mcp
```

`github:cwen63970-coder/dsh-everything-mcp` works as well — the same field also takes an npm package name or a local
directory. Click **Install**. `dsh-everything-mcp` then appears under *Installed plugins*, and the five
`mcp__everything__*` tools are available immediately: the patch inserts the mcp-client row itself and resolves its
own server path inside the profile, so nothing needs a restart.

> [!NOTE]
> The manifest declares `dsh.bundle.platforms: ["win32"]`, so on Linux or macOS the host skips the whole bundle
> instead of failing at spawn time.

From the command line the equivalent is:

```text
plugin_manager install_bundle   target=github:cwen63970-coder/dsh-everything-mcp
```

## Requirements

1. Windows 10/11.
2. Everything installed and running — it owns the index.

   If it is not installed yet, get the installer from the official site:
   <https://www.voidtools.com/downloads/> (pick the build with `x64` in the filename on 64-bit Windows, the one
   without it on 32-bit). The defaults are fine. Once the tray icon shows up Everything is running and building the
   index by itself; being able to search means it is ready — this plugin only queries it and needs no setup of its
   own. Measured against `Everything 1.4.1.1032`.

   Recommended settings under **Tools → Options → General**:
   - **Everything Service**: ticked (ticking it asks for elevation once, to install the service)
   - **Run on system startup**: ticked
   - **Run as administrator**: unticked

   That way the service indexes NTFS and Everything itself stays a standard-user process: it is ready right after
   boot, and programs launched from Everything do not inherit elevation. Running as administrator works too
   (verified), but then the service is not needed and everything you open from Everything runs elevated; Windows 10+
   also will not auto-start an elevated program, so leave it off if you want it at boot.

   **The index scope is Everything's too**: local fixed NTFS volumes only by default; USB disks and network
   locations are added inside Everything (see [Troubleshooting](#troubleshooting)).
3. DSH `0.2.2-rc.1` or a later `0.2.x`.

## What it does

It fills the "where is this file?" gap for the agent. Everything's local index is exposed as five MCP tools,
so the model can look up candidates by name, path, extension, size, date or regex across **every indexed
volume** in one query, and get back paths plus metadata.

| Tool | Purpose |
|---|---|
| `everything_search` | Full Everything query syntax: name, path, size, date, wildcards, regex, `dupe:`, `empty:`, … |
| `everything_count` | Count matches without transferring rows |
| `everything_status` | Is Everything running / index loaded / elevated |
| `everything_recent` | Recently modified files (`dm:lastNdays`) |
| `everything_duplicates` | Names that occur more than once |

This removes three pain points of native tooling: guessing a root directory and walking it with `glob`;
scanning a drive with `Get-ChildItem -Recurse` (minutes, and often blocked by permissions); and hand-rolling
size/date/extension filters. Tool names match a hand-written MCP configuration (`mcp__everything__*`), so no
profile edits are needed.

## What it does not do

- **No content search** — it returns paths, sizes and timestamps, never file contents. Use `read` / `grep`.
- **No writes** — it never modifies files or Everything's configuration.
- **No index of its own** — the index belongs to Everything; this plugin only queries it.
- **No proof of absence** — an empty result means "not in the current index", not "does not exist".
  Out-of-scope volumes and an in-progress rebuild both cause misses; `everything_status` reports `index busy`.

## Limits

| Dimension | Reality |
|---|---|
| Coverage | Only what Everything indexes. Default is local fixed NTFS volumes; FAT/exFAT, removable and network locations must be added in Everything |
| Completeness | Depends on index state. While `everything_status` reports `index busy`, results can be badly incomplete (measured: 66 vs 6,769 items for the same path) |
| Speed | Milliseconds for ordinary name lookups; seconds for size/date sorting, a large `max`, or `duplicates` scans over tens of thousands of rows |
| Data flow | Lookups run locally with no third-party search service, but returned paths and metadata enter the conversation context |
| Platform | Windows only; Everything must be installed and running |

## How it works

```
DSH --(MCP stdio)-->  mcp/everything-mcp.mjs  --(line JSON)-->  mcp/everything-ipc.exe  --(WM_COPYDATA)-->  Everything.exe
```

Everything's command line has no "search and print results" switch, which is why the bridge exists: zero
dependencies, and it speaks Everything's own IPC protocol. Its source is `mcp/everything-ipc.cs`, which the
packaged `everything-ipc.exe` is compiled from; the same build also runs on GitHub Actions — every push
recompiles it from source on windows-latest and prints its SHA-256 in the log.

**No dependency on `node` being on PATH.** `command` is `process.execPath` — DeepSeek Harness itself, which is
Electron — with `env.ELECTRON_RUN_AS_NODE=1`. The host binary then behaves as a Node runtime (measured
`v24.21.0`), so the plugin works on machines without a standalone Node install.

## Query gotchas

**The full syntax is simply Everything's own search syntax.** The plugin hands `query` to Everything verbatim — it does not parse, whitelist or rewrite it, so anything you can type into Everything works here as well. These three bite most often:

| Query | Meaning |
|---|---|
| `dm:last7days` | The last seven days. A bare `dm:7days` is **not** that — it silently matches unrelated old files |
| `\node_modules` | Matches the full path, so it hits that whole subtree; `node_modules` only hits entries named that |
| `ext:exe` | Filter by extension. Type macros `exe:` / `doc:` / `pic:` are user-defined filters; undefined ones return zero silently |

`everything_search` and `everything_count` also take four switches each: `regex` (treat `query` as a regular
expression, in Everything's own dialect), `matchCase`, `wholeWord`, and `matchPath` (match the full path instead of
just the file name). They apply to that one call only and are independent of the same-named toggles in Everything's
GUI — with the GUI's regex mode on, plugin queries are still parsed as ordinary Everything syntax.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `Everything is not running` | Start Everything; it owns the index |
| A drive, USB disk or network location returns nothing | All of it lives under **Tools → Options**. Only local fixed NTFS volumes are indexed by default: NTFS USB disks → **Index → NTFS**, tick *Automatically include new removable volumes*; FAT (exFAT / FAT32) → **Index → Folders**, add the drive letter (on Everything 1.5 tick the same option under **Index → FAT**); network locations → **Index → Folders** as well |
| Tools missing entirely | Check `everything_status`; confirm *Plugins → Installed plugins* lists `dsh-everything-mcp` |
| Empty results for a file you just created | Check that the volume is indexed and **Monitor changes** is ticked (non-NTFS and network locations must be added separately); while Everything is rebuilding its index the file is not there either — wait for `index loaded: true`; otherwise run `Everything.exe -reindex` |
| A query times out with `no reply` | Everything is still rebuilding its index (`index loaded: false` in `everything_status`); wait for it to finish |

## License

MIT. Not affiliated with or endorsed by [voidtools](https://www.voidtools.com/), the official home of Everything.
