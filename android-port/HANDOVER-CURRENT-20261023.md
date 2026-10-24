# BazaarPlusPlus 安卓移植 — 交接文档（当前状态）

> 生成：2026-10-23 23:40 (+08:00)
> 读者：接手 Agent。本文是最小可用上下文，读完即可动手。
> 详细背景见同目录 `HANDOVER-ANDROID-PORT.md`（505 行，含 14 条 IL2CPP 崩溃根因，**不要回退那些修复**）。

---

## 0. 一句话现状

三个模块（配装 / 图鉴 / 历史）的代码修复已落盘，**但当前源码编译失败（1 个 error），且 `dist` 里的 modpack 落后于源码**。
接手第一件事必须是修编译错误，再重新打包、上真机验收。

| 项目 | 状态 |
|---|---|
| 源码 | 已修改（23:29），含三模块修复 |
| 编译 | ❌ **失败**，1 error / 94 warning |
| `dist/bpp-modpack.zip` | ⚠️ 17:51 产物，**早于源码**，不可用于验收 |
| 真机 | ⚠️ 游戏进程已退出，前台停在 `com.bepinex.android/.StubActivity` |
| Git 仓库 | ✅ 干净，`TAP-APIA/bazaarplusplus-android`，4 个提交 |

---

## 1. 🔴 阻断项：编译失败（必须先修）

```
NativeInspectionPresentation.cs(142,13): error CS0103:
当前上下文中不存在名称"ConcealMobileTrayBackdrops"
```

- 文件：`src/BazaarPlusPlus/GameInterop/Tooltips/NativeInspectionPresentation.cs`
- 第 142 行调用了 `ConcealMobileTrayBackdrops();`，但**该方法从未定义**（子代理只写了调用点）。
- 同类已有实现 `ConcealMobileDecoration(Behaviour)` 在第 493 行，记录到 `_mobileDecoration` 并 `enabled = false`。

修法二选一（推荐 A）：

**A. 补实现**（符合第 128-129 行注释意图「mobile side tray 的 viewport mask 与 tray backdrop 不得裁切或遮挡描述」）：
在 `#if BPP_ANDROID` 区域内新增方法，遍历 `controller.RootCanvasComponent` 下的托盘背景 `Graphic`，复用 `ConcealMobileDecoration` 登记，保证 `Dispose` 时能按 `_mobileDecoration` 还原。

**B. 删调用**：若确认托盘背景不构成遮挡，直接删第 142 行。

> 注意：无论 A/B，都必须保证 `Dispose()` 能还原 `_mobileDecoration` 里登记的组件，否则退出检视后图鉴 UI 会残留隐藏元素。

---

## 2. 构建 / 部署 / 打包

```powershell
$env:DOTNET_ROLL_FORWARD="Major"
& C:\Users\administrator\.dotnet\dotnet.exe build `
  "D:\thebarzaar\BazaarPlusPlus\bazaarplusplus-mod\src\BazaarPlusPlus\BazaarPlusPlus.csproj" `
  -c Debug -p:BppAndroid=true -p:ManagedPath=D:\thebarzaar\android-managed `
  -p:NuGetAudit=false -p:TreatWarningsAsErrors=false --nologo
```

- 部署：`D:\thebarzaar\dist\install-to-device.ps1`（**必须双写** `BepInEx/plugins` 与 `modpacks/bpp/plugins`，否则启动时被 modpack 覆盖）
- 打包：`dist\package-modpack.ps1 -Zip`
- 启动器：`dist\package-launcher.ps1`
- 打包/改 `dist` 后**必须**重跑 `SHA256SUMS.txt`（当前 16:07 版已过期）

**签名**：平板现装 Debug 证书。覆盖升级必须
`-KeyStore "$env:USERPROFILE\.android\debug.keystore" -KeyAlias androiddebugkey`
用 `tools/launcher/bpp.keystore` 会 `INSTALL_FAILED_UPDATE_INCOMPATIBLE`。

---

## 3. 设备与 adb

- 设备：vivo PA2573 平板，Android 16，无线调试。本轮地址 `192.168.0.14:37699`（**端口会变**，`adb mdns services` 常为空，不能据此判断设备离线）
- adb 真实路径（PATH 里没有 adb，别直接敲 `adb`）：
  `D:\thebarzaar\tools\android-build\sdk\platform-tools\adb.exe`
  备用：`D:\thebarzaar\tools\platform-tools\platform-tools\adb.exe`
- 启动器包 `com.bepinex.android`，游戏进程 `com.bepinex.android:game`
- 锁屏 PIN：`<device-pin>`

**启动流程定位用 `uiautomator dump` 按 `text=` 找元素，禁止硬编码坐标。**

---

## 4. 🖼️ MCP 读图：唯一正确用法（今天踩坑最多）

`user-vps-image` MCP 可用，`send_image_to_vps` 上传成功。**真正容易错的是拿到 URL 之后怎么用。**

正确链路（三步）：

1. `send_image_to_vps(path="D:\...\shot.png")` → 得到 `https://<vps-host>:8443/f/<uuid>`
2. 把该 URL **原样**作为 `image_url` 放进请求
3. 请求发给 **VPS BYOK**：`https://<vps-host>:8443/v1/chat/completions`
   - 模型：`gemini-3.5-flash`（VPS 共 73 个模型，视觉选它稳定）
   - `gemini-3.1-pro` **无权限**，会报 `Publisher model ... was not found`
   - VPS 侧车会把 `/f` URL 自动改写成 base64 再转发上游

**绝对禁止（MCP 文档明确列出的错误做法，我全部踩过）**：

- ❌ 本机下载 URL 再转 base64
- ❌ 把 URL 发给 `codex-router` / 本地 `/v1/chat/completions`
- ❌ 塞给非视觉模型（如 `ollama-native-deepseek-v4-1-flash-1017`，VPS 不认识该名字，报 `model_not_found`）
- ❌ 以为 `Read(本地路径)` 就等于上游能看见图

**Windows 实操要点**：

- PowerShell 5.1 不支持 `-SkipCertificateCheck`；用 `curl.exe -k` 最省事
- 用 `curl.exe --data-binary @req.bin -o resp.bin` 走**字节**，再用 `[Text.Encoding]::UTF8.GetString()` 解析，避免中文变 `ÐÐ»Ñ`
- `max_tokens` 给 2500+，800 会被截断

已验证可用的一次调用：

```powershell
$token = [IO.File]::ReadAllText("<local-provider-key-path>").Trim()
$payload = '{"model":"gemini-3.5-flash","max_tokens":2500,"stream":false,"messages":[{"role":"user","content":[{"type":"text","text":"<中文提问>"},{"type":"image_url","image_url":{"url":"<上一步的 /f URL>"}}]}]}'
[IO.File]::WriteAllBytes("req.bin", [Text.Encoding]::UTF8.GetBytes($payload))
curl.exe -s -S -X POST "https://<vps-host>:8443/v1/chat/completions" `
  -H "Authorization: Bearer $token" -H "Content-Type: application/json; charset=utf-8" `
  --data-binary "@req.bin" -k -o resp.bin --max-time 170
```

**读图必须配客观量化**：视觉模型对同一张图会给出互相矛盾的结论（实测一次说「32 张纯白」、一次说「正常占位槽」）。
所以对「白块 / 遮挡 / 缺失」这类判断题，**先做像素统计或裁切放大，再让模型判读局部放大图**，不要只信整图描述。

---

## 5. 🔍 图鉴白块：诊断结论已更正

**旧结论（错误）**：遮罩层把卡牌盖住了 → 方向是 UI 层级 / alpha。

**新结论（有证据）**：**卡牌立绘纹理加载失败，底板白色露出**。

证据链：

1. 像素统计：卡牌网格区纯白占比 **47%–54%**（`white-check.png` 54.2%，`cur-2331.png` 47.2%）
2. 2× 放大单格判读：格子内部**完全纯白，无画、无图标、无文字**；金属边框、倒角高光、底部宝石徽章**全部渲染正常**
3. 关键细节：**白色矩形溢出金属边框之外**（边框外侧露出白边）→ 说明是降级默认底板被拉伸，并非按格裁切
4. 代码注释直接对应症状：

```startLine:34:38:filepath
// Returns null on load failure; the caller proceeds without a material assignment, which
// matches the game's own behaviour for invalid art keys (blank face, frame still drawn).
```

`src/BazaarPlusPlus/Game/CollectionPanel/Grid/CollectionCardArtCache.cs` 在 Addressables 失败时返回 `null`，调用方跳过材质赋值 → 只剩白底板 + 边框。

**排查方向（按可能性排序）**：

1. **Addressables catalog 未在 Android 上加载**（打包未含 catalog / 路径不对）
2. **artKey 大小写不匹配** — Windows 不敏感、Android 敏感，这是移动端移植第一坑
3. **纹理压缩格式不兼容**（ASTC/ETC2 解码失败 → 采样白）

**验证入口**：日志事件 `collection_panel.card_art.degraded` 带 `reason_code`（取值 `AddressablesLoadFailed` / `AddressablesLoadException`）。
当前设备 logcat 缓冲区已被冲掉（只剩 16:37 前记录），**需重启游戏复现后再抓**。

---

## 6. 三模块状态与下一步

### 6.1 配装 / Live Build（十胜阵容取不到数据）

- 现象：`https://cdn.bazaarplusplus.com/metrics/v6/builds/latest.json` 返回 **HTTP 404**，回退到内嵌 seed（仅 3 条样本）
- 相关代码：
  - `Core/Game/LiveBuildPanel/Recommendations/BuildRecommendationRepository.cs`
  - `Core/Game/LiveBuildPanel/Recommendations/TenWinBuildCorpus.cs`（严格解析 + 匹配）
  - `src/BazaarPlusPlus/Data/BuildRecommendations/builds.json`（内嵌兜底）
- 下一步：确认远程 404 是**上游变更**还是**路径版本号变了**；若上游确实下线，需明确「只用内嵌 seed」的产品口径，并保证 seed 数量足够（当前 3 条明显不足）

### 6.2 图鉴 / Collection

- 白块问题见 §5（**主方向已改为资源管线**）
- 检视窗遮罩：`NativeInspectionPresentation.cs` 有未完成的 `ConcealMobileTrayBackdrops` 调用（见 §1），修完才算收口
- 相关文件：`Game/CollectionPanel/**`、`Patches/CollectionPanel/CollectionTierTooltipPreview.cs`

### 6.3 历史 / History & Replay

- 已完成（**不要回退**）：bootstrap2→6 回放链修复；血条 NRE（`HealthBarController.ForceEmptyHealthVisual` 读未 `Init` 的 `HealthBarControl`）已真机确认修复，`ForceEmptyHealthVisual` / `NullReferenceException` / `Fatal signal` 计数均为 0
- **仍未闭合的缺口**：回放跑完只有 `combat_replay.playback.started`，`playback.succeeded/degraded/failed` 全为 0
  - 机制：终态事件只由 `ReplayPlaybackLogOperation.Complete()` 发出，由 **ReplayState 退出**驱动
  - `Events.ReplayEnded` 的托管 handler `OnNativeReplayEnded` 首行 `if (!currentNativeRecording && !managedRecordedReplay) return;`，本次 `source=local_saved`、`record_video=false` **直接 return**
  - 本质：回放没有自然结束到 recap/Continue 那一步，**不是日志机制坏了**
  - 下一步：确认回放到尾是否自动弹 recap（`BoardManager.ShowRecapView`）、`BoardRecapReplayButtonsController.Show` 的 Harmony Postfix 是否触发
- 未证明：血条**数值正确性**（截图有绿条和数字，但 OCR 未读出可靠数值，只能说「渲染了」）
- 另：同一会话内第二次进入回放未触发 `playback.started`，待查

---

## 7. 写代码前必读（IL2CPP 硬约束）

强转一律 `AsIl2Cpp<T>()` / `AsRectTransform()` / `AsImage()`；字段用 `BppFieldAccessor`；世界坐标用 `GetWorldCornersManaged`。

- `foreach (Transform child in parent)` → `InvalidCastException`，改 `GetChild(i)`
- `Il2CppSystem.Nullable<T>` 的 `default` 是 null，必须 `new`
- 异步必须 `.AsManagedTask()`，禁止 `while (!t.IsCompleted) await Task.Yield()`
- `Dictionary<InstanceId, X>` 托管泛型调用会 SIGSEGV，改走 `TheBazaar.Data.GetCard` 等原生入口
- Harmony 降级 10 个：3 个确认不可修（`LoadArt` / `GetPassiveTooltipBlock` / `SetupLockVariantPanel` Transpiler），其余 7 个看 `LogOutput.log` 的 `Failed to patch`
- ⚠️ `sub_logoutput.log` 里 4 条 `Cannot assign method return type Il2CppSystem.Threading.Tasks.Task` 是 **10-22 12:12 旧证据**，早于 `#if BPP_ANDROID` 改造，别按它改回去

---

## 8. 验收流程（主线程负责）

1. 修编译错误 → 构建 0 error
2. 重新打包 modpack + 重跑 `SHA256SUMS.txt`
3. `install-to-device.ps1` **只推 modpack**（不要重装 APK，避免动到内部数据目录里的回放文件）
4. 冷启动，确认 `plugin.initialization.succeeded`，进程活过 60 秒
5. `adb logcat` 清空后复现图鉴 → **抓 `collection_panel.card_art.degraded` 的 `reason_code`**（这是解白块的关键一步）
6. 逐面板截图 → `send_image_to_vps` → VPS BYOK `gemini-3.5-flash` 读图 → **配像素统计交叉验证**
7. 回放：历史 → 选局 → 播放 → 走到 recap/Continue → 确认出现 `playback.succeeded|degraded`

---

## 9. 文档地图

| 文件 | 作用 |
|---|---|
| `HANDOVER-CURRENT-20261023.md` | 本文，最小交接（敏感信息已脱敏） |
| `HANDOVER-ANDROID-PORT.md` | 主文档：环境、14 条崩溃修复、踩坑表、代码地图 |
| `HANDOVER.md` | 2026-10-21 初始任务书 + metadata v39 逆向结论 |
| `dist/README.md` | 交付与安装说明（16:09 版，已过期） |

---

## 10. 交付物核对

| 文件 | 时间 | 状态 |
|---|---|---|
| `dist/BepInExLauncher-android-updated.apk` | 10-23 14:20 | bootstrap4 交付版，平板已装 |
| `dist/bpp-modpack.zip` | 10-23 17:51 | ⚠️ **落后于源码，需重新打包** |
| `dist/SHA256SUMS.txt` | 10-23 16:07 | ⚠️ 需重跑 |

Git：`D:\thebarzaar\repo-publish\bazaarplusplus-android` → `https://github.com/TAP-APIA/bazaarplusplus-android`（public，master 已保护：禁强推、禁删除）
工作区源码在 `D:\thebarzaar\BazaarPlusPlus\bazaarplusplus-mod\`，**publish 仓库是快照，不是工作副本**。
