# dsh-everything（中文说明）

> English: [README.md](README.md)

把 [Everything](https://www.voidtools.com/) 的文件名索引接进 DeepSeek Harness（DSH）的 **bundle 插件**。
Windows 专用：Everything 只运行在 Windows 上。

最后更新：2026-11-07（M0 完成，插件已接管 `everything` 这个名字）

## 这是什么

一个「纯配置 bundle」：不导出 Host 插件、不导出客户端 UI，只在自己的 `cordis.patch.yml` 里插入一条
`@deepseek-ai/dsh-mcp-client`，指向本包自带的 MCP 服务端：

```
DSH --(MCP stdio)-->  mcp/everything-mcp.mjs  --(JSON 行)-->  mcp/everything-ipc.exe  --(WM_COPYDATA)-->  Everything.exe
```

`mcp/everything-ipc.exe` 是零依赖的 C# 桥，直接实现 Everything 的 `WM_COPYDATA` IPC 协议
（Everything 自带命令行没有「搜索并输出结果」的开关）。源码 `mcp/everything-ipc.cs` 同仓，可用
Windows 自带的 .NET Framework 编译器重建。

## 当前状态（M0 已完成：路径注入方案成立）

| 项 | 状态 |
|---|---|
| 包结构（manifest + patch + MCP 服务端 + 图标 + locale） | 已建 |
| patch 内用 `!!js` 从 `ctx.baseUrl` 推出包内绝对路径 | **2026-11-07 实测通过** |
| 工具可用性（`mcp__everything__*`） | 5 个工具，`status` / `count` / `recent` 实测正常 |
| 热加载 | `plugin_manager install_bundle` / 改 patch 后**无需重启**即生效 |
| 接管 `serverName: everything` | 已完成，手工条目已从 profile 移除 |
| 非 Windows 降级（M1） | **完成**：`dsh.bundle.platforms: ["win32"]` 由宿主整体跳过；patch 里另有 `disabled: !!js process.platform !== 'win32'` 兜底；服务端在非 win32 上也给明确错误 |
| 包元数据 + 双语文档（M2） | **完成**：`version 0.1.0`、`private` 已去除、`engines.dsh`、`platforms`、`scripts/smoke.mjs`、`README.md`(EN) + `README.zh-CN.md` |
| CI + 发布（M3） | 工作流已写好（`.github/workflows/build.yml`），**发布待仓库地址与凭据** |

### 安装 = 一条命令，零手工配置

```text
plugin_manager install_bundle  target=link:D:\dsh\projects\dsh-everything
# 或发布后：target=<npm 包名> / github:<owner>/<repo>
```

装完即用：`serverName: everything`，工具名 `mcp__everything__everything_search` 等，与手工配置版本完全一致。

**老机器迁移（实测全程无需重启）**：如果装插件之前已经手工配过 `serverName: everything` 的条目，
按下面三步走，每步都是热加载生效：

1. 把旧条目的 `serverName` 改个名（例如 `everything_old`）——旧实例重载，`everything` 这个名字被释放；
2. 把插件的 `serverName` 设成 `everything`——插件接管，工具名保持不变；
3. 删掉旧条目。

关键点：**直接删旧条目不会释放名字**（宿主进程里的 `activeServerNames` 仍占着），必须先改名。
见下面第 4 条实测。

### 两个已验证的实现细节

1. **`!!js` 的作用域**：bundle patch 里的表达式由 Loader 以 `new Function("ctx", …)` 求值，`ctx.baseUrl`
   是 **profile 目录**（`…/.dsh/profiles/desktop/`），因此 `node_modules/<包名>/…` 可以直接拼出来。
   表达式里不要出现 `: `（冒号+空格），YAML 会把它当成映射分隔符——第一次尝试就是这么坏的。
2. **不要用 `node` 命令**：`process.execPath` 指向 DeepSeek Harness 自己（Electron），
   配合 `env.ELECTRON_RUN_AS_NODE: '1'` 它就是一个 Node 运行时（实测 `v24.21.0`）。
   这样插件不依赖用户 PATH 里有没有 node。

3. **bundle 补丁的边界**：bundle 层的 patch 只作用于本层可见的 entry 树，**看不到用户层**
   （`profiles/<name>/cordis.patch.yml`）。试过在 bundle patch 里写
   `- id: mcp-everything` + `disabled: true` 想自动接管历史条目——无效，实测仍报
   `serverName "everything" is already in use`。旧条目只能用上面「改名 → 接管 → 删除」的方式处理。
4. **serverName 只在持有它的实例 dispose 时释放，删条目不释放**（2026-11-07 实测）：
   - 删掉旧条目后热加载完成，工具消失，但再装插件仍报 `already in use`——名字还占着；
   - 把旧条目的 `serverName` 改名后，旧实例按「配置变更 → dispose → 重新 apply」走一遍，
     名字立刻释放，插件随即注册成功，全程没有重启。
   所以热加载对**新增/改配置**友好，对**删除**不回收这份进程内状态。

### 实测证据（2026-11-07）

接管后的唯一实例（旧条目已从 profile 移除，`everything_old` 也已消失）：

```
PID 28316  "…\DeepSeek Harness.exe"  …/profiles/desktop/node_modules/dsh-everything/mcp/everything-mcp.mjs
PID 21984  everything-ipc.exe        D:\dsh\projects\dsh-everything\mcp\everything-ipc.exe
```

`mcp__everything__everything_status` → `1.4.1.1032 running and queryable` / `index loaded: true` / `admin: false`；
`mcp__everything__everything_recent(D:\dsh\projects\dsh-everything\, last 1 day)` → 10 项。

**并存验证**（16:54）：手工条目（`node.exe …\everything-mcp\mcp-server.mjs`）与本插件
（`DeepSeek Harness.exe …\dsh-everything\mcp\everything-mcp.mjs` + 包内 `everything-ipc.exe`）
两个 MCP server 同时在线、各自独立应答，互相不影响。所以「先验证插件、再替换旧条目」是可执行的，
不需要先拆掉在用的那套。

## 目录内容

| 路径 | 作用 |
|---|---|
| `package.json` | bundle manifest：`dsh.bundle.patch`、`exports` 的 locale/icon |
| `cordis.patch.yml` | 唯一的挂载声明：insert 一条 mcp-client 记录 |
| `mcp/everything-mcp.mjs` | MCP stdio 服务端（5 个工具，零 npm 依赖） |
| `mcp/everything-ipc.exe` / `.cs` | Everything IPC 桥（预编译 + 源码） |
| `locale/en.json`、`locale/zh.json` | 插件页的标题与描述 |
| `icon.svg` | 插件图标 |

## 运行前提

1. 安装 Everything（默认路径 `C:\Program Files (x86)\Everything\Everything.exe`）并让它**正在运行**；
2. 建议装服务并随登录启动，GUI 用普通权限：
   ```powershell
   & 'C:\Program Files (x86)\Everything\Everything.exe' -install-service          # 管理员
   & 'C:\Program Files (x86)\Everything\Everything.exe' -install-run-on-system-startup
   ```
3. GUI 里取消勾选 **Run as administrator**，并确认 `ntfs_volume_load_recent_changes=1,1,1`
   （否则 Everything 关机期间的增删改不会补进索引）。

## 备份与回滚

- 仓库根 `D:\dsh` 是 git 工作区，`git checkout -- projects/dsh-everything` 可恢复本目录。
- 装进 profile 后，从 `profiles/web/package.json` 的 `dependencies` 与 `dsh.profile.bundles` 里移除即可卸载。

## 安装（开发期）

```text
plugin_manager install_bundle  target=link:D:\dsh\projects\dsh-everything
```

会写进 profile 的 `dependencies`（`link:`）与 `dsh.profile.bundles`，并热加载生效。

## 待办

- [x] 验证 `!!js` 路径方案（2026-11-07：成立，工具已注册）
- [ ] 非 Windows 平台降级（不注册工具 / 明确报错）
- [ ] 桥的源码重建说明与 CI（windows-latest + csc）
- [ ] 包名、版本、LICENSE 头、双语文档定稿后发 GitHub
