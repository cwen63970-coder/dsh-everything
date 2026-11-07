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
<img src="assets/banner.svg" alt="dsh-everything" width="100%" />
<br />
</div>

---

Everything 在本机维护一个「全部卷、全部文件名」的索引。这个插件把它接到智能体手上：一次查询几毫秒返回，
不需要走目录树，也不依赖任何云端服务。

装完之后你会多出 5 个工具，名字都是 `mcp__everything__*`，与手工配置 MCP 的写法完全一致。
不需要改 profile、不需要重启、不需要在首次使用时做任何选择。

## 运行前提

1. Windows 10/11；
2. Everything 已安装并正在运行。推荐这样配置——由服务索引 NTFS，GUI 不需要提权：
   ```powershell
   & 'C:\Program Files (x86)\Everything\Everything.exe' -install-service          # 管理员，装一次
   & 'C:\Program Files (x86)\Everything\Everything.exe' -install-run-on-system-startup
   ```
   然后在 Everything 选项里取消勾选 **Run as administrator**，并确认
   `%APPDATA%\Everything\Everything.ini` 里 `ntfs_volume_load_recent_changes=1,1,1`
   （写成 `0,0,0` 时启动不回放 USN 日志，关机期间的增删改永远补不进索引）；
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
IPC 协议，源码在 `mcp/everything-ipc.cs`，用 Windows 自带的 .NET Framework 编译器即可重建：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:anycpu /optimize+ `
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
  '/out:everything-ipc.exe' 'everything-ipc.cs'
```

> [!NOTE]
> `csc.exe` 对正斜杠路径有坑：`mcp/everything-ipc.cs` 会被解析成 `.\everything-ipc.cs`（报 CS1504），
> 路径请用反斜杠。

**自引用路径。** patch 是静态 YAML，但 `args` 需要绝对路径。Loader 的 `!!js` 表达式以 profile 目录
（`ctx.baseUrl`）为作用域求值，所以可以直接拼出 `node_modules/dsh-everything/mcp/everything-mcp.mjs`，
不写死任何用户路径。两个坑：表达式里出现 `: `（冒号+空格）会被 YAML 当成映射分隔符；`pathname` 在 Windows
上会得到 `/C:/…`，要把前导斜杠去掉。

**不依赖 PATH 里的 `node`。** `command` 用 `process.execPath`（DeepSeek Harness 自己，即 Electron）配
`env.ELECTRON_RUN_AS_NODE=1`，宿主可执行文件随即作为 Node 运行时工作（实测 `v24.21.0`），因此没装独立
Node 的机器也能用。

## 查询语法

这里只列三条最容易踩的，完整语法见 Everything 自己的帮助（Help → Search Syntax）：

| 写法 | 含义 |
|---|---|
| `dm:last7days` | 最近 7 天。裸写 `dm:7days` **不是**这个意思，会静默匹配到无关的旧文件 |
| `\node_modules` | 匹配完整路径，命中该目录子树；`node_modules` 只命中名字真的叫这个的项 |
| `ext:exe` | 按扩展名过滤。类型宏 `exe:` / `doc:` / `pic:` 是用户自定义过滤器，没定义时静默返回 0 |

## 疑难排解

| 现象 | 原因 / 处理 |
|---|---|
| `Everything is not running` | 启动 Everything；索引归它所有 |
| `serverName "everything" is already in use` | 已有另一条 mcp-client 用了同名 server，删掉或改名那条手工配置 |
| 工具完全不出现 | 先看 `everything_status`；再确认 profile 的 `dsh.profile.bundles` 里有 `dsh-everything` |
| 刚建的文件搜不到 | `ntfs_volume_load_recent_changes` 设置或索引过期；执行 `Everything.exe -reindex` |

## 开发

```powershell
# 协议自检（不需要 Everything 在跑）
node scripts\smoke.mjs

# 本机状态
node mcp\everything-mcp.mjs status

# 本地改完直接装进 profile
plugin_manager install_bundle   target=link:D:\dsh\projects\dsh-everything
```

CI 在 `windows-latest` 上跑：`csc` 编译桥 → 打印 SHA-256 → `node --check` → `scripts/smoke.mjs` → 上传产物。

## 许可证

MIT。与 voidtools 无隶属或背书关系。
