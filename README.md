<div align="center">

**简体中文** · [English](README.en.md) · [繁體中文](README.zh-TW.md)

# dsh-everything-mcp

### 把 Everything 的文件名索引接进 DeepSeek Harness 的只读检索插件。

**一次查询定位已索引的文件或文件夹——返回路径与元数据，不读内容、不改文件。**

[![build](https://github.com/cwen63970-coder/dsh-everything-mcp/actions/workflows/build.yml/badge.svg)](https://github.com/cwen63970-coder/dsh-everything-mcp/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/cwen63970-coder/dsh-everything-mcp?style=flat-square&color=2f6feb&label=release)](https://github.com/cwen63970-coder/dsh-everything-mcp/releases)
[![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=flat-square&logo=windows&logoColor=white)](#运行前提)
[![DSH](https://img.shields.io/badge/DSH-%E2%89%A5%200.2.2--rc.1-4B32C3?style=flat-square)](#运行前提)
[![MCP tools](https://img.shields.io/badge/MCP-5%20tools-orange?style=flat-square)](#它的作用)
[![license](https://img.shields.io/badge/license-MIT-f4c430?style=flat-square)](LICENSE)

[安装](#安装) · [运行前提](#运行前提) · [它的作用](#它的作用) · [能力边界](#能力边界) · [工作原理](#工作原理) · [查询语法](#查询语法) · [疑难排解](#疑难排解)

<code>插件 → 安装第三方插件：https://github.com/cwen63970-coder/dsh-everything-mcp</code>

</div>

> [!IMPORTANT]
> **仅 Windows。** Everything 本身是 Windows 程序，IPC 桥也是一个小的 .NET 可执行文件。
> Everything 必须已安装并**正在运行**——索引归它所有，本插件只是查询方。

<div align="center">
<br />
<img src="assets/hero.png" alt="dsh-everything-mcp：Everything 搜索结果与 agent 的工具调用" width="100%" />
<br />
</div>

---

## 安装

在 DSH 里打开 **插件 → 安装第三方插件**，在「添加插件」的「包名或地址」里填 npm 包名：

```text
@xhtzlc/dsh-everything-mcp
```

也可以填仓库地址 `github:cwen63970-coder/dsh-everything-mcp`——这一栏同时接受 npm 包名、仓库地址和本地目录，
然后点「安装」。装完在「已安装插件」里会看到 `@xhtzlc/dsh-everything-mcp`，5 个 `mcp__everything__*` 工具立刻
可用——插件的 patch 会自己插入 mcp-client 那条配置、算出包内服务端的绝对路径，不需要重启。

> [!NOTE]
> 插件在 manifest 里声明了 `dsh.bundle.platforms: ["win32"]`，Linux / macOS 上宿主会**整体跳过**它，
> 而不是在 spawn 阶段报错。

习惯用命令的话，等价写法是：

```text
plugin_manager install_bundle   target=@xhtzlc/dsh-everything-mcp
```

## 运行前提

1. Windows 10/11；
2. Everything 已安装并正在运行——索引归它所有。

   还没装的话，从官网下载安装版：<https://www.voidtools.com/zh-cn/downloads/>（64 位系统选文件名带 `x64`
   的那个，32 位系统选不带 `x64` 的），一路默认装完即可。启动后托盘出现它的图标就说明在运行，索引由它自己建，
   能搜到文件就算就绪——本插件只是查询方，不需要为它做任何配置。本插件在 `Everything 1.4.1.1032` 上实测。

   推荐在 **工具 → 选项 → 常规** 里设为这样：
   - **Everything 服务**：勾选（首次勾选会请求一次管理员权限，用来装服务）
   - **随系统自启动**：勾选
   - **以管理员身份运行**：不勾选

   这样由服务去索引 NTFS，Everything 本体保持普通权限：开机即可用，从 Everything 里打开的程序也不会跟着提权。
   勾了最后一项也照样能用（实测），只是那时不再需要服务，而从 Everything 打开的程序都会提权；另外 Windows 10
   起不会自动启动提权程序，想开机就用，就别勾它；

   **索引范围也归 Everything 管**：默认只索引本地 NTFS 固定卷，U 盘/移动硬盘/网络位置要在它里面单独添加
   （见[疑难排解](#疑难排解)）。
3. DSH `0.2.2-rc.1` 或更新的 `0.2.x`。

## 它的作用

给智能体补上「文件在哪」这一环：把 Everything 的本地索引包装成 5 个 MCP 工具，让模型按名称、路径、
扩展名、大小、日期或正则，在**全部已索引的卷**上一次查询拿到候选路径与元数据。

| 工具 | 用途 |
|---|---|
| `everything_search` | 完整 Everything 查询语法：名称、路径、大小、日期、通配符、正则、`dupe:`、`empty:` … |
| `everything_count` | 只统计命中数，不传输结果行 |
| `everything_status` | Everything 是否在运行 / 索引是否就绪 / 是否提权 |
| `everything_recent` | 最近修改的文件（`dm:lastNdays`） |
| `everything_duplicates` | 重名文件 |

对模型来说，这解决的是原生手段的三个痛点：不用猜根目录、逐层 `glob`；不用 `Get-ChildItem -Recurse`
扫盘（分钟级，还会被权限打断）；不用自己拼大小/日期/扩展名的组合过滤。工具名与手工配置 MCP 的写法一致
（`mcp__everything__*`），不用改 profile。

## 它不做什么

- **不读文件内容**——只返回路径、大小、时间等元数据；要看内容请用 `read` / `grep`。
- **不写文件、不改 Everything 配置**——所有调用都是只读查询。
- **不维护自己的索引**——索引归 Everything，插件只是查询方。
- **不能证明「文件不存在」**——空结果只说明「当前索引里没有」。索引范围外、索引重建中、未监控的卷
  都会造成假阴性；`everything_status` 会报告 `index busy`。

## 能力边界

| 维度 | 实际情况 |
|---|---|
| 覆盖范围 | 仅 Everything 已索引的内容。默认是本地 NTFS 固定卷；FAT/exFAT、可移动盘、网络位置需在 Everything 里单独添加 |
| 结果完整性 | 依赖索引状态。`everything_status` 显示 `index busy` 时结果可能大幅缺失（实测同一路径 66 条 vs 6,769 条） |
| 速度 | 常规按名查询毫秒级；按大小/日期排序、`max` 很大、或 `duplicates` 扫描数万行时会到秒级 |
| 数据流向 | 检索在本地完成，不经过第三方搜索服务；但**返回的路径与元数据会进入对话上下文** |
| 平台 | 仅 Windows；Everything 必须已安装并正在运行 |

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
| 某个盘、U 盘或网络位置搜不到 | 都在 **工具 → 选项** 里改。默认只索引本地 NTFS 固定卷：NTFS 格式的 U 盘/移动硬盘 →「索引 → NTFS」勾「自动包含新增的可移动卷」；FAT 格式（exFAT / FAT32）→「索引 → 文件夹」把盘符加进去（Everything 1.5 则在「索引 → FAT」里勾同样的选项）；网络位置 → 同样走「索引 → 文件夹」 |
| 工具完全不出现 | 先看 `everything_status`；再确认「插件 → 已安装插件」里有 `dsh-everything-mcp` |
| 刚建的文件搜不到 | 先确认该卷在索引里、且勾了「监控变化」（非 NTFS 与网络位置要单独添加）；Everything 正在重建索引时也查不到，等 `index loaded: true`；仍不行则执行 `Everything.exe -reindex` |
| 查询超时、报 `no reply` | Everything 正在重建索引，还没加载完（`everything_status` 里 `index loaded: false`）；等它加载完再查 |

## 许可证

MIT。与 [voidtools](https://www.voidtools.com/)（Everything 的官方站点）无隶属或背书关系。
