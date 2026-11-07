<div align="center">

**简体中文** · [English](README.en.md) · [繁體中文](README.zh-TW.md)

# dsh-everything

### 把 Everything 的文件名索引接进 DeepSeek Harness，装完即用的一个 bundle。

**毫秒级找到任意文件——不用遍历目录，不用记住它在哪个盘。**

[![build](https://github.com/cwen63970-coder/dsh-everything/actions/workflows/build.yml/badge.svg)](https://github.com/cwen63970-coder/dsh-everything/actions/workflows/build.yml)
[![release](https://img.shields.io/badge/release-v0.1.0-2f6feb?style=flat-square)](https://github.com/cwen63970-coder/dsh-everything/releases)
[![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=flat-square&logo=windows&logoColor=white)](#运行前提)
[![DSH](https://img.shields.io/badge/DSH-%E2%89%A5%200.2.2--rc.1-4B32C3?style=flat-square)](#运行前提)
[![MCP tools](https://img.shields.io/badge/MCP-5%20tools-orange?style=flat-square)](#它能做什么)
[![license](https://img.shields.io/badge/license-MIT-f4c430?style=flat-square)](LICENSE)

[安装](#安装) · [它能做什么](#它能做什么) · [工作原理](#工作原理) · [查询语法](#查询语法) · [疑难排解](#疑难排解)

<code>plugin_manager install_bundle&nbsp;&nbsp;target=github:cwen63970-coder/dsh-everything</code>

</div>

> [!IMPORTANT]
> **仅 Windows。** Everything 本身是 Windows 程序，IPC 桥也是一个小的 .NET 可执行文件。
> Everything 必须已安装并**正在运行**——索引归它所有，本插件只是查询方。

<div align="center">
<br />
<img src="assets/hero.png" alt="dsh-everything：Everything 搜索结果与 agent 的工具调用" width="100%" />
<br />
</div>

---

Everything 在本机维护一个「全部卷、全部文件名」的索引。这个插件把它接到智能体手上：一次查询几毫秒返回，
不需要走目录树，也不依赖任何云端服务。

装完之后你会多出 5 个工具，名字都是 `mcp__everything__*`，与手工配置 MCP 的写法完全一致。
不需要改 profile、不需要重启、不需要在首次使用时做任何选择。

## 运行前提

1. Windows 10/11；
2. Everything 已安装并正在运行——索引归它所有。推荐在 **工具 → 选项 → 常规** 里设为这样：
   - **Everything 服务**：勾选（首次勾选会请求一次管理员权限，用来装服务）
   - **随系统自启动**：勾选
   - **以管理员身份运行**：不勾选

   这样由服务去索引 NTFS，Everything 本体保持普通权限：开机即可用，从 Everything 里打开的程序也不会跟着提权。
   勾了最后一项也照样能用（实测），只是那时不再需要服务，而从 Everything 打开的程序都会提权；另外 Windows 10
   起不会自动启动提权程序，想开机就用，就别勾它；
3. DSH `0.2.2-rc.1` 或更新的 `0.2.x`。

## 安装

```text
plugin_manager install_bundle   target=<npm 包名> | github:<owner>/dsh-everything | link:<本地路径>
```

插件的 patch 会自己插入 mcp-client 那条配置、算出包内服务端的绝对路径、注册工具。加载器对新条目是热生效的，
所以装完立即可用。

插件在 manifest 里声明了 `dsh.bundle.platforms: ["win32"]`，Linux / macOS 上宿主会**整体跳过**它，
而不是在 spawn 阶段报错。

## 它能做什么

| 工具 | 用途 |
|---|---|
| `everything_search` | 完整 Everything 查询语法：名称、路径、大小、日期、通配符、正则、`dupe:`、`empty:` … |
| `everything_count` | 只统计命中数，不传输结果行 |
| `everything_status` | Everything 是否在运行 / 索引是否就绪 / 是否提权 |
| `everything_recent` | 最近修改的项（`dm:lastNdays`） |
| `everything_duplicates` | 重名文件 |

## 工作原理

```
DSH --(MCP stdio)-->  mcp/everything-mcp.mjs  --(逐行 JSON)-->  mcp/everything-ipc.exe  --(WM_COPYDATA)-->  Everything.exe
```

Everything 自带的命令行没有「搜索并输出结果」的开关，所以必须有这座桥：它零依赖，直接实现 Everything 的
IPC 协议。源码在 `mcp/everything-ipc.cs`，包里的 `everything-ipc.exe` 就是它编译出来的；同样的编译也在
GitHub Actions 上跑——每次推送都会在 windows-latest 上从源码重新编一份，并在日志里给出它的 SHA-256。

**不依赖 PATH 里的 `node`。** `command` 用 `process.execPath`（DeepSeek Harness 自己，即 Electron）配
`env.ELECTRON_RUN_AS_NODE=1`，宿主可执行文件随即作为 Node 运行时工作（实测 `v24.21.0`），因此没装独立
Node 的机器也能用。

## 查询语法

**完整语法就是 Everything 自己的搜索语法。** 插件把 `query` 原样交给 Everything 解析——不解析、不白名单、不改写，所以能在 Everything 里写的任何写法都能直接写进 `query`。下面三条是最容易踩的：

| 写法 | 含义 |
|---|---|
| `dm:last7days` | 最近 7 天。裸写 `dm:7days` **不是**这个意思，会静默匹配到无关的旧文件 |
| `\node_modules` | 匹配完整路径，命中该目录子树；`node_modules` 只命中名字真的叫这个的项 |
| `ext:exe` | 按扩展名过滤。类型宏 `exe:` / `doc:` / `pic:` 是用户自定义过滤器，没定义时静默返回 0 |

`everything_search` 和 `everything_count` 还各带四个开关：`regex`（把 `query` 当正则，Everything 自己的正则方言）、`matchCase`、`wholeWord`、`matchPath`（匹配完整路径，而不是只匹配文件名）。它们只作用于这一次调用，与 Everything GUI 里的同名勾选框互不影响——GUI 的正则开关打开时，插件查询仍按普通语法解析。

## 疑难排解

| 现象 | 原因 / 处理 |
|---|---|
| `Everything is not running` | 启动 Everything；索引归它所有 |
| 某个盘、U 盘或网络位置搜不到 | Everything 默认只索引本地 NTFS 卷；非 NTFS 与网络位置要在 Everything 的「索引 → 文件夹」里手动添加 |
| 工具完全不出现 | 先看 `everything_status`；再确认 profile 的 `dsh.profile.bundles` 里有 `dsh-everything` |
| 刚建的文件搜不到 | 索引过期或启动时没回放 USN 日志：确认 `%APPDATA%\Everything\Everything.ini` 里 `ntfs_volume_load_recent_changes=1,1,1`（`0,0,0` 时关机期间的增删改补不进索引），或执行 `Everything.exe -reindex` |
| 查询超时、报 `no reply` | Everything 正在重建索引，还没加载完（`everything_status` 里 `index loaded: false`）；等它加载完再查 |

## 许可证

MIT。与 [voidtools](https://www.voidtools.com/)（Everything 的官方站点）无隶属或背书关系。
