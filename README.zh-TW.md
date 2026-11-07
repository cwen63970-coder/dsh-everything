<div align="center">

[简体中文](README.md) · [English](README.en.md) · **繁體中文**

# dsh-everything

### 把 Everything 的檔案名稱索引接進 DeepSeek Harness，裝完即用的一個 bundle。

**毫秒級找到任何檔案——不用逐一走訪資料夾，不用記得它在哪個磁碟。**

[![build](https://github.com/cwen63970-coder/dsh-everything/actions/workflows/build.yml/badge.svg)](https://github.com/cwen63970-coder/dsh-everything/actions/workflows/build.yml)
[![release](https://img.shields.io/badge/release-v0.1.0-2f6feb?style=flat-square)](https://github.com/cwen63970-coder/dsh-everything/releases)
[![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?style=flat-square&logo=windows&logoColor=white)](#執行前提)
[![DSH](https://img.shields.io/badge/DSH-%E2%89%A5%200.2.2--rc.1-4B32C3?style=flat-square)](#執行前提)
[![MCP tools](https://img.shields.io/badge/MCP-5%20tools-orange?style=flat-square)](#它能做什麼)
[![license](https://img.shields.io/badge/license-MIT-f4c430?style=flat-square)](LICENSE)

[安裝](#安裝) · [它能做什麼](#它能做什麼) · [運作原理](#運作原理) · [查詢語法](#查詢語法) · [疑難排解](#疑難排解)

<code>plugin_manager install_bundle&nbsp;&nbsp;target=github:cwen63970-coder/dsh-everything</code>

</div>

> [!IMPORTANT]
> **僅限 Windows。** Everything 本身是 Windows 程式，IPC 橋梁也是一個小的 .NET 執行檔。
> Everything 必須已安裝且**正在執行**——索引歸它所有，此外掛只是查詢方。

<div align="center">
<br />
<img src="assets/hero.png" alt="dsh-everything：Everything 搜索结果与 agent 的工具调用" width="100%" />
<br />
</div>

---

Everything 會在本機維護一份「所有磁碟區、所有檔案名稱」的索引。這個外掛把它交到代理手上：一次查詢幾毫秒回傳，
不需要走訪目錄樹，也不依賴任何雲端服務。

安裝完成後會多出 5 個工具，名稱都是 `mcp__everything__*`，與手動設定 MCP 的寫法完全一致。
不需要改 profile、不需要重新啟動、首次使用也不需要做任何選擇。

## 執行前提

1. Windows 10/11；
2. Everything 已安裝且正在執行——索引歸它所有。建議在 **工具 → 選項 → 一般** 裡設成這樣：
   - **Everything 服務**：勾選（第一次勾選會要求一次系統管理員權限，用來安裝服務）
   - **隨系統自啟動**：勾選
   - **以系統管理員身分執行**：不勾選

   這樣由服務去索引 NTFS，Everything 本體保持一般權限：開機即可使用，從 Everything 裡開啟的程式也不會跟著提權。
   勾了最後一項也照樣能用（實測），只是那時不再需要服務，而從 Everything 開啟的程式都會提權；另外 Windows 10
   起不會自動啟動提權程式，想開機就用，就別勾它；
3. DSH `0.2.2-rc.1` 或更新的 `0.2.x`。

## 安裝

```text
plugin_manager install_bundle   target=<npm 套件名> | github:<owner>/dsh-everything | link:<本機路徑>
```

外掛的 patch 會自行插入 mcp-client 那筆設定、算出套件內服務端的絕對路徑、註冊工具。載入器對新項目是熱生效的，
所以裝完立即可用。

外掛在 manifest 裡宣告了 `dsh.bundle.platforms: ["win32"]`，在 Linux / macOS 上宿主會**整體略過**它，
而不是在 spawn 階段報錯。

## 它能做什麼

| 工具 | 用途 |
|---|---|
| `everything_search` | 完整 Everything 查詢語法：名稱、路徑、大小、日期、萬用字元、正規表示式、`dupe:`、`empty:` … |
| `everything_count` | 只統計命中數量，不回傳結果列 |
| `everything_status` | Everything 是否在執行 / 索引是否就緒 / 是否提權 |
| `everything_recent` | 最近修改的項目（`dm:lastNdays`） |
| `everything_duplicates` | 同名的檔案 |

## 運作原理

```
DSH --(MCP stdio)-->  mcp/everything-mcp.mjs  --(逐行 JSON)-->  mcp/everything-ipc.exe  --(WM_COPYDATA)-->  Everything.exe
```

Everything 內建的命令列沒有「搜尋並輸出結果」的開關，所以必須有這座橋梁：它零相依，直接實作 Everything 的
IPC 協定。原始碼在 `mcp/everything-ipc.cs`，套件裡的 `everything-ipc.exe` 就是它編譯出來的；同樣的編譯也在
GitHub Actions 上跑——每次推送都會在 windows-latest 上從原始碼重新編譯一份，並在日誌裡印出它的 SHA-256。

**不依賴 PATH 裡的 `node`。** `command` 使用 `process.execPath`（DeepSeek Harness 本身，即 Electron）搭配
`env.ELECTRON_RUN_AS_NODE=1`，宿主執行檔隨即以 Node 執行環境運作（實測 `v24.21.0`），因此沒安裝獨立
Node 的機器也能使用。

## 查詢語法

**完整語法就是 Everything 自己的搜尋語法。** 外掛把 `query` 原樣交給 Everything 解析——不解析、不白名單、不改寫，所以能在 Everything 裡寫的任何寫法都能直接寫進 `query`。下面三條是最容易踩到的：

| 寫法 | 含義 |
|---|---|
| `dm:last7days` | 最近 7 天。直接寫 `dm:7days` **不是**這個意思，會默默比對到無關的舊檔案 |
| `\node_modules` | 比對完整路徑，命中該目錄子樹；`node_modules` 只命中名稱真的是這個的項目 |
| `ext:exe` | 依副檔名篩選。類型巨集 `exe:` / `doc:` / `pic:` 是使用者自訂的篩選器，未定義時默默回傳 0 筆 |

`everything_search` 與 `everything_count` 還各帶四個開關：`regex`（把 `query` 當成正規表示式，Everything 自己的方言）、`matchCase`、`wholeWord`、`matchPath`（比對完整路徑，而不是只比對檔名）。它們只作用於這一次呼叫，與 Everything GUI 裡同名的勾選框互不影響——GUI 的正規表示式開關打開時，外掛查詢仍以一般語法解析。

## 疑難排解

| 現象 | 原因 / 處理 |
|---|---|
| `Everything is not running` | 啟動 Everything；索引歸它所有 |
| 某個磁碟、隨身碟或網路位置搜不到 | Everything 預設只索引本機 NTFS 磁碟區；非 NTFS 與網路位置要在 Everything 的「索引 → 資料夾」手動加入 |
| 工具完全不出現 | 先看 `everything_status`；再確認 profile 的 `dsh.profile.bundles` 裡有 `dsh-everything` |
| 剛建立的檔案搜不到 | 索引過期或啟動時沒回放 USN 日誌：確認 `%APPDATA%\Everything\Everything.ini` 裡 `ntfs_volume_load_recent_changes=1,1,1`（`0,0,0` 時關機期間的新增、刪除、修改補不進索引），或執行 `Everything.exe -reindex` |
| 查詢逾時、回報 `no reply` | Everything 正在重建索引，還沒載入完成（`everything_status` 裡 `index loaded: false`）；等它載入完再查 |

## 授權條款

MIT。與 [voidtools](https://www.voidtools.com/)（Everything 的官方網站）無隸屬或背書關係。
