[简体中文](README.md) · [English](README.en.md) · **繁體中文**

# dsh-everything

把 [Everything](https://www.voidtools.com/) 的檔案名稱索引接進 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)
的 **bundle 外掛**。

**僅限 Windows**：Everything 本身是 Windows 程式，IPC 橋梁也是一個小的 .NET 執行檔。

## 它做什麼

透過 `@deepseek-ai/dsh-mcp-client` 註冊 5 個 MCP 工具（`mcp__everything__*`），查詢直接落到機器上既有的
Everything 索引，毫秒級回傳，不需要逐一走訪資料夾：

| 工具 | 用途 |
|---|---|
| `everything_search` | 完整 Everything 查詢語法：名稱、路徑、大小、日期、萬用字元、正規表示式、`dupe:`、`empty:` … |
| `everything_count` | 只統計命中數量，不回傳結果列 |
| `everything_status` | Everything 是否在執行 / 索引是否就緒 / 是否提權 |
| `everything_recent` | 最近修改的項目（`dm:lastNdays`） |
| `everything_duplicates` | 同名的檔案 |

```
DSH --(MCP stdio)-->  mcp/everything-mcp.mjs  --(逐行 JSON)-->  mcp/everything-ipc.exe  --(WM_COPYDATA)-->  Everything.exe
```

Everything 內建的命令列沒有「搜尋並輸出結果」的開關，所以必須有這座橋梁：它零相依，直接實作 Everything 的
IPC 協定。原始碼在 `mcp/everything-ipc.cs`，可用 Windows 內建的 .NET Framework 編譯器重建：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:anycpu /optimize+ `
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
  '/out:everything-ipc.exe' 'everything-ipc.cs'
```

> `csc.exe` 對正斜線路徑有問題：`mcp/everything-ipc.cs` 會被解析成 `.\everything-ipc.cs`（回報 CS1504），
> 路徑請使用反斜線。

## 執行前提

1. Windows 10/11；
2. Everything 已安裝且**正在執行**——索引歸它所有，此外掛只是查詢方。建議設定：
   ```powershell
   & 'C:\Program Files (x86)\Everything\Everything.exe' -install-service          # 系統管理員，裝一次
   & 'C:\Program Files (x86)\Everything\Everything.exe' -install-run-on-system-startup
   ```
   接著在 Everything 選項裡取消勾選 **Run as administrator**；並保持
   `%APPDATA%\Everything\Everything.ini` 裡 `ntfs_volume_load_recent_changes=1,1,1`——設成 `0,0,0` 時
   Everything 啟動不會回放 USN 日誌，關機期間的新增、刪除、修改永遠不會補進索引。
3. DSH `0.2.2-rc.1` 或更新的 `0.2.x`。

## 安裝

```text
plugin_manager install_bundle   target=<npm 套件名> | github:<owner>/dsh-everything | link:<本機路徑>
```

裝完即可使用：外掛的 patch 會自行插入 mcp-client 那筆設定、算出套件內服務端的絕對路徑、註冊工具。不需要手動改設定，
也不需要重新啟動（載入器對新項目是熱生效的）。

外掛在 manifest 裡宣告了 `dsh.bundle.platforms: ["win32"]`，因此在 Linux / macOS 上宿主會**整體略過**這個
bundle，而不是在 spawn 階段報錯。

## 兩個關鍵實作細節

**自我參照路徑。** patch 是靜態 YAML，但 `args` 需要絕對路徑。Loader 的 `!!js` 運算式以 profile 目錄
（`ctx.baseUrl`）為作用域求值，所以可以直接組出 `node_modules/dsh-everything/mcp/everything-mcp.mjs`，
不必寫死任何使用者路徑。兩個坑：運算式裡出現 `: `（冒號+空格）會被 YAML 當成對應分隔符號；`pathname` 在
Windows 上會得到 `/C:/…`，要去掉前導斜線。

**不依賴 PATH 裡的 `node`。** `command` 使用 `process.execPath`（DeepSeek Harness 本身，即 Electron）搭配
`env.ELECTRON_RUN_AS_NODE=1`，宿主執行檔隨即以 Node 執行環境運作（實測 `v24.21.0`），因此沒安裝獨立
Node 的機器也能使用。

## 疑難排解

| 現象 | 原因 / 處理 |
|---|---|
| `Everything is not running` | 啟動 Everything；索引歸它所有。 |
| `serverName "everything" is already in use` | 已有另一筆 mcp-client 使用同名 server，請刪除或改名那筆手動設定。 |
| 工具完全不出現 | 先看 `everything_status`；再確認 profile 的 `dsh.profile.bundles` 裡有 `dsh-everything`。 |
| 剛建立的檔案搜不到 | `ntfs_volume_load_recent_changes` 設定或索引過期；執行 `Everything.exe -reindex`。 |

## 語法提醒

- 日期位移必須帶前綴：`dm:last7days` 才是「最近 7 天」，`dm:7days` 會默默比對到無關的舊檔案；
- 前導反斜線才會比對完整路徑：`\node_modules` 命中該目錄子樹，`node_modules` 只命中名稱；
- 類型巨集 `exe:` / `doc:` / `pic:` 是使用者自訂的篩選器，未定義時會默默回傳 0 筆，請改用 `ext:exe`。

## 授權條款

MIT。與 voidtools 無隸屬或背書關係。
