**简体中文** · [English](README.en.md) · [繁體中文](README.zh-TW.md)

# dsh-everything

把 [Everything](https://www.voidtools.com/) 的文件名索引接进 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)
的 **bundle 插件**。

**仅 Windows**：Everything 本身是 Windows 程序，IPC 桥也是一个小的 .NET 可执行文件。

## 它做什么

通过 `@deepseek-ai/dsh-mcp-client` 注册 5 个 MCP 工具（`mcp__everything__*`），查询直接落到机器上已有的
Everything 索引，毫秒级返回，不需要遍历目录：

| 工具 | 用途 |
|---|---|
| `everything_search` | 完整 Everything 查询语法：名称、路径、大小、日期、通配符、正则、`dupe:`、`empty:` … |
| `everything_count` | 只统计命中数，不传输结果行 |
| `everything_status` | Everything 是否在运行 / 索引是否就绪 / 是否提权 |
| `everything_recent` | 最近修改的项（`dm:lastNdays`） |
| `everything_duplicates` | 重名文件 |

```
DSH --(MCP stdio)-->  mcp/everything-mcp.mjs  --(逐行 JSON)-->  mcp/everything-ipc.exe  --(WM_COPYDATA)-->  Everything.exe
```

Everything 自带的命令行没有「搜索并输出结果」的开关，所以必须有这座桥：它零依赖，直接实现 Everything 的
IPC 协议。源码在 `mcp/everything-ipc.cs`，可以用 Windows 自带的 .NET Framework 编译器重建：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:anycpu /optimize+ `
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
  '/out:everything-ipc.exe' 'everything-ipc.cs'
```

> `csc.exe` 对正斜杠路径有坑：`mcp/everything-ipc.cs` 会被解析成 `.\everything-ipc.cs`（报 CS1504），
> 路径请用反斜杠。

## 运行前提

1. Windows 10/11；
2. Everything 已安装并**正在运行**——索引归它所有，本插件只是查询方。推荐配置：
   ```powershell
   & 'C:\Program Files (x86)\Everything\Everything.exe' -install-service          # 管理员，装一次
   & 'C:\Program Files (x86)\Everything\Everything.exe' -install-run-on-system-startup
   ```
   然后在 Everything 选项里取消勾选 **Run as administrator**；并保持
   `%APPDATA%\Everything\Everything.ini` 里 `ntfs_volume_load_recent_changes=1,1,1`——写成 `0,0,0` 时
   Everything 启动不回放 USN 日志，关机期间的增删改永远不会补进索引。
3. DSH `0.2.2-rc.1` 或更新的 `0.2.x`。

## 安装

```text
plugin_manager install_bundle   target=<npm 包名> | github:<owner>/dsh-everything | link:<本地路径>
```

装完即用：插件的 patch 会自己插入 mcp-client 那条配置、算出包内服务端的绝对路径、注册工具。不需要手工改配置，
也不需要重启（加载器对新条目是热生效的）。

插件在 manifest 里声明了 `dsh.bundle.platforms: ["win32"]`，所以在 Linux / macOS 上宿主会**整体跳过**这个
bundle，而不是在 spawn 阶段报错。

## 两个关键实现点

**自引用路径。** patch 是静态 YAML，但 `args` 需要绝对路径。Loader 的 `!!js` 表达式以 profile 目录
（`ctx.baseUrl`）为作用域求值，所以可以直接拼出 `node_modules/dsh-everything/mcp/everything-mcp.mjs`，
不写死任何用户路径。两个坑：表达式里出现 `: `（冒号+空格）会被 YAML 当成映射分隔符；`pathname` 在 Windows
上会得到 `/C:/…`，要把前导斜杠去掉。

**不依赖 PATH 里的 `node`。** `command` 用 `process.execPath`（DeepSeek Harness 自己，即 Electron）配
`env.ELECTRON_RUN_AS_NODE=1`，宿主可执行文件随即作为 Node 运行时工作（实测 `v24.21.0`），因此没装独立
Node 的机器也能用。

## 故障排查

| 现象 | 原因 / 处理 |
|---|---|
| `Everything is not running` | 启动 Everything；索引归它所有。 |
| `serverName "everything" is already in use` | 已有另一条 mcp-client 用了同名 server，删掉或改名那条手工配置。 |
| 工具完全不出现 | 先看 `everything_status`；再确认 profile 的 `dsh.profile.bundles` 里有 `dsh-everything`。 |
| 刚建的文件搜不到 | `ntfs_volume_load_recent_changes` 设置或索引过期；执行 `Everything.exe -reindex`。 |

## 语法提醒

- 日期偏移必须带前缀：`dm:last7days` 才是「最近 7 天」，`dm:7days` 会静默匹配到无关的旧文件；
- 前导反斜杠才匹配完整路径：`\node_modules` 命中该目录子树，`node_modules` 只命中名字；
- 类型宏 `exe:` / `doc:` / `pic:` 是用户自定义过滤器，没定义时静默返回 0，请用 `ext:exe`。

## 许可证

MIT。与 voidtools 无隶属或背书关系。
