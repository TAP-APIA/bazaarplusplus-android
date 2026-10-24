# BazaarPlusPlus 安卓移植 — 交接文档（当前状态）

> 生成：2026-10-23 23:40 (+08:00)；**更新：2026-10-24 11:00**（编译阻断清零，三模块与回放终态按真机验收闭合）
> 读者：接手 Agent。本文是最小可用上下文，读完即可动手。
> 详细背景见同目录 `HANDOVER-ANDROID-PORT.md`（505 行，含 14 条 IL2CPP 崩溃根因，**不要回退那些修复**）。
> 逐轮实测记录见同目录 `ACCEPTANCE-ROUND1-20261024.md`（本文与之冲突时以它为准）。

---

## 0. 一句话现状

三个模块（配装 / 图鉴 / 历史）的修复已落盘，并已**真机验收通过**（Round 6 全部通过，见 `ACCEPTANCE-ROUND1-20261024.md` §12）；源码**编译 0 error**。
剩下的是交付物一致性：`dist/bpp-modpack.zip` 已于 10-24 09:51 重打包，但 `SHA256SUMS.txt` 还停在 02:03，**必须重跑并重新核对**（见 §10）。

| 项目 | 状态 |
|---|---|
| 源码 | ✅ 三模块 + 图鉴检视修复齐备（最新提交 `dcd7189`） |
| 编译 | ✅ **0 error**（warning 数待核，最近记录 95） |
| `dist/bpp-modpack.zip` | ⚠️ 10-24 09:51 产物（`BazaarPlusPlus.dll` 4,274,688 B），**晚于** `SHA256SUMS.txt` 的 02:03 版，清单待重跑 |
| 真机 | ✅ 三面板 + 回放终态均已验收；本轮日志 `_r8_LogOutput.log`（10-24 10:55） |
| Git 仓库 | ✅ 干净，`TAP-APIA/bazaarplusplus-android`，6 个提交 |

---

## 1. ✅ 已完成：编译阻断已清零（原 `ConcealMobileTrayBackdrops` 未定义）

**现状**：源码编译 **0 error**，已无 CS0103 阻断；本节从「阻断项」移入已完成清单。

| 事项 | 结果 | 证据 |
|---|---|---|
| `ConcealMobileTrayBackdrops`（CS0103） | ✅ 已解除 | `c75c12a` 补上实现（+39 行，复用 `ConcealMobileDecoration` 登记，`Dispose` 时还原） |
| 检视窗遮罩方案 | ✅ 已被 `dcd7189` 整体替换 | 改为**保留**游戏原生抽屉与托盘背景，只在抽屉 rect 上自绘一块不透明底板；`ConcealMobileTrayBackdrops` / `ConcealMobileDecoration` 方法族随该提交删除 |

> ⚠️ 接手注意：现在在源码里搜 `ConcealMobileTrayBackdrops` **搜不到是正常的**——方法已随 `dcd7189` 删除，不是又丢了。不要再按旧文档「补实现」，否则会与抽屉方案打架；当前实现见 §6.2。

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
- 打包/改 `dist` 后**必须**重跑 `SHA256SUMS.txt`（当前 02:03 版早于 09:51 的 modpack，仍待重跑，见 §10）

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

> ⚠️ **本节结论已被 Round 1 推翻，仅作历史记录**：同一构建下历史棋盘卡与放大卡立绘全部正常，冷启动后 124 卡零白块、`card_art.degraded=0`，说明 catalog / 材质 / 着色器 / 纹理压缩均健康。真实成因是**久运行 + 反复开关面板后的缓存与失败黑名单退化**（`CollectionCardArtCache` 的 LRU + `_failedKeys` 永久黑名单，4 条静默 `return` 不写日志）。以下「排查方向」**不要**再按资源管线查；加固项见 §6.2 与 `ACCEPTANCE-ROUND1-20261024.md` §3。

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

### 6.1 配装 / Live Build（十胜阵容）—— ✅ 已修好，真机实测通过

- 现状：**已修好**。真机实测**阵容库 5,689 套**，对局中**切换阵容 `1 / 277`**（匹配成功），**十胜率 100%**（依据 `ACCEPTANCE-ROUND1-20261024.md` §12.2，截图 `_r6_builds.png`）
- 本轮日志复证：`live_build_panel.corpus.ready source=cache build_count=5689`
- 真实根因（旧文档的「远程 404」是误判）：
  - 代码真正用的是 `bpp-metrics-v6.bazaarplusplus.com/builds/latest.json`，**返回 200 但只有 3 套、仅 Dooley**；旧文档写的 `cdn.bazaarplusplus.com` 主机整站不存在（404）
  - `TenWinBuildCatalogFactory` 的 `CacheDuration=20h` 且按**缓存文件写入时间**判陈旧 → 永不刷新 → 一直服务那 3 套
  - `LiveBuildPanel.Open()` 开头的 `_candidateState.Clear()` 让候选为空 → `FindRecommendations` 直接返回空 → 面板显示「0 / 0」
- 已落地的修法：
  - 新增 `FallbackRemoteCatalogSource`：多源按**密度择优**（≥64 套且 ≥4 英雄），命中 legacy `bpp-metrics.bazaarplusplus.com`（5,689 套 / 8 英雄 / 1,123 卡，线格式与 v6 兼容）
  - `TenWinBuildCatalogFactory`：`RemoteUrl` → `RemoteUrls[]`（v6 优先、legacy 兜底）、超时 10s → 30s、`CacheDuration` 20h → **1h**
  - `LiveBuildPanel.SeedCandidatesFromBoard()`：用场上物品预置候选，消除「点开就是 0/0」
- 相关代码：`Core/Game/LiveBuildPanel/Recommendations/BuildRecommendationRepository.cs`、`TenWinBuildCorpus.cs`、`FallbackRemoteCatalogSource.cs`；`src/BazaarPlusPlus/Data/BuildRecommendations/builds.json` 只是内嵌兜底（1 套假 GUID），**不要**当语料用

### 6.2 图鉴 / Collection

- **方案 A 已落地**（提交 `dcd7189`「inspect collection cards in the native mobile drawer」），三段式布局：
  - **左半屏**：BPP 自绘的放大卡（`NativeInspectionPresentation` 的 root canvas，`ScreenSpaceOverlay`，`sorting_order=3002`）
  - **右半屏**：复用游戏自带的 Mobile Lock Mode **原生抽屉**（`Scroll View`），保留其 `CanvasScaler`、viewport mask、滚动与描述布局；BPP 只抬排序并把抽屉吸附到原生 fade-in 的 show 位置，`Release` 时还原
  - **不透明底板**：原生抽屉自带的 `lockModeBackgroundImage` 会被禁用（否则压暗放大卡），禁用后图鉴筛选列会透过描述显示，因此在抽屉 rect 上自绘 `DrawerBackdrop` 补一块不透明底板（`raycastTarget=false`，不吃点击）
- 运行时证据：`collection_panel.inspection.android_drawer render_mode=ScreenSpaceOverlay override_sorting=false sorting_order=3002 left=2111 width=985`
- 白块：Round 1 已推翻「资源管线」旧结论（同构建下历史棋盘卡与放大卡立绘全正常）；根因是久运行 + 反复开关面板后的缓存/黑名单退化，冷启动即恢复。`card_art.degraded` 全程 0。**加固项仍未落地，属隐患**：`_failedKeys` 加 TTL、4 条静默 `return` 补 `ReportDegraded`、`_cardImage` 实为 `RawImage` 需同步清 `texture`（详见 `ACCEPTANCE-ROUND1-20261024.md` §3.4 / §十.3）
- 相关文件：`Game/CollectionPanel/**`、`GameInterop/Tooltips/NativeInspectionPresentation.cs`、`Patches/CollectionPanel/CollectionTierTooltipPreview.cs`

### 6.3 历史 / History & Replay —— ✅ 回放终态已闭合

- 已完成（**不要回退**）：bootstrap2→6 回放链修复；血条 NRE（`HealthBarController.ForceEmptyHealthVisual` 读未 `Init` 的 `HealthBarControl`）已真机确认修复，`ForceEmptyHealthVisual` / `NullReferenceException` / `Fatal signal` 计数均为 0
- **「回放只有 `playback.started`」的缺口今天已由真机验证闭合**（`_r8_LogOutput.log`，battle_id=00f38840）：

```
[Info   :BazaarPlusPlus] [BPP][CombatReplay] event=combat_replay.playback.started battle_id=00f38840 source=local_saved record_video=false
[Info   :BazaarPlusPlus] [BPP][CombatReplay] event=combat_replay.playback.ended_observed native_recording=false managed_recording=false recap_action_bound=true handled=false battle_id=00f38840
[Info   :BazaarPlusPlus] [BPP][CombatReplay] event=combat_replay.playback.succeeded battle_id=00f38840 source=local_saved end_reason_code=saved_replay_exit duration_ms=165101 reason_code=none degradation_count=0 rollback_status=not_required
```

**结论（已验证，不再是「待查」）**：

1. **不是日志机制坏了**。终态事件由 **`ReplayState` 退出**驱动，`ReplayPlaybackLogOperation.Complete()` 只在回放真正结束后才发。
2. `ended_observed` 里 `recap_action_bound=true`、`handled=false`（判读：recap 动作已绑定到原生按钮，但托管 handler 没有消费它，实际由原生路径退出回放）→ 随后 `ReplayState` 退出 → `succeeded` 落地。
3. `OnNativeReplayEnded` 首行 `if (!currentNativeRecording && !managedRecordedReplay) return;` 依然会短路（本局 `source=local_saved`、`record_video=false`），但它**不参与**终态事件，不影响闭合。

**验收步骤（必须照做，否则会误判成「只有 started」）**：

1. 历史 → 选一局 → 播放；
2. **等回放自己走到 recap**（`BoardManager.ShowRecapView`）；
3. **点 recap 上的「继续」**（`BoardRecapReplayButtonsController.Show` 的 Harmony Postfix 绑定；坐标只从 `overlay_panels.ui_raycast.rect` 的 `center` 取，别用截图估）；
4. 回到历史后立刻抓日志，应同时出现 `playback.started` → `playback.ended_observed` → `playback.succeeded`。本局 `duration_ms=165101`（约 2 分 45 秒），**别提前收工**。

此前验收停在「播完」、没点继续，所以才只看到 `playback.started`——这轮就是这么闭合的。

- 未证明：血条**数值正确性**（截图有绿条和数字，但 OCR 未读出可靠数值，只能说「渲染了」）
- 另：同一会话内第二次进入回放未触发 `playback.started`，**待查**（本轮日志仍只有一次 `playback.started`）

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

1. 构建 0 error（✅ 已达成；warning 数按需记录）
2. 重新打包 modpack + 重跑 `SHA256SUMS.txt`（⚠️ 未完成，见 §10）
3. `install-to-device.ps1` **只推 modpack**（不要重装 APK，避免动到内部数据目录里的回放文件）
4. 冷启动，确认 `plugin.initialization.succeeded`，进程活过 60 秒
5. 白块：冷启动下已不复现（`collection_panel.card_art.degraded` 全程 0，说明命中的是 4 条**不写日志**的静默 `return`）。要复现得**久运行 + 反复开关面板**，再抓 `card_art.degraded` 与 `card.bind_degraded`；加固项见 §6.2
6. 逐面板截图 → `send_image_to_vps` → VPS BYOK `gemini-3.5-flash` 读图 → **配像素统计交叉验证**
7. 回放：历史 → 选局 → 播放 → **等 recap 出现并点「继续」** → 确认 `playback.started` + `playback.ended_observed` + `playback.succeeded|degraded` 三条齐全（✅ 已达成，见 §6.3）

---

## 9. 文档地图

| 文件 | 作用 |
|---|---|
| `HANDOVER-CURRENT-20261023.md` | 本文，最小交接（敏感信息已脱敏） |
| `HANDOVER-ANDROID-PORT.md` | 主文档：环境、14 条崩溃修复、踩坑表、代码地图 |
| `HANDOVER.md` | 2026-10-21 初始任务书 + metadata v39 逆向结论 |
| `ACCEPTANCE-ROUND1-20261024.md` | 第 1 轮逐项验收记录（§3 白块、§8 十胜 5,689、§12 Round 6 全通过）——**冲突时以它为准** |
| `dist/README.md` | 交付与安装说明（2026-10-24 版，签名说明已按 P2-1 更正） |

---

## 10. 交付物核对

| 文件 | 时间 | 状态 |
|---|---|---|
| `dist/BepInExLauncher-android-updated.apk` | 10-23 14:20 | ✅ bootstrap4 交付版，平板已装；签名 `CN=Android Debug`，SHA-256 `80379CA3BF211233…`，与 `SHA256SUMS.txt` 第 1 项一致 |
| `dist/bpp-modpack.zip` | 10-24 09:51 | ⚠️ **需重新核对**：内含 `BazaarPlusPlus.dll` 4,274,688 B @ 09:51（晚于验收时的 4,231,680 B @ 00:14）；当前 SHA-256 `AB91137F5B1C3FCE…`，`SHA256SUMS.txt` 记的是 `29917398A24F1631…` |
| `dist/SHA256SUMS.txt` | 10-24 02:03 | ⚠️ **需重跑**：早于 09:51 的 modpack。逐项自校验 **4/6 一致**，不一致的两项正是 `bpp-modpack.zip` 与 `dist/README.md`（本文档一改，第 6 项必然变） |

> 打包 / 改 `dist` 后**必须**重跑 `SHA256SUMS.txt` 并自校验。

Git：`D:\thebarzaar\repo-publish\bazaarplusplus-android` → `https://github.com/TAP-APIA/bazaarplusplus-android`（public，master 已保护：禁强推、禁删除）
工作区源码在 `D:\thebarzaar\BazaarPlusPlus\bazaarplusplus-mod\`，**publish 仓库是快照，不是工作副本**。
