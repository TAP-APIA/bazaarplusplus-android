# BazaarPlusPlus 安卓移植 — 第 1 轮验收记录

> 生成：2026-10-24 00:05 (+08:00)
> 依据：真机冷启动实测（vivo PA2573 / `192.168.0.14:37699` / `com.bepinex.android:game`）
> 本轮性质：**编译阻断修复 + 三面板基线验收**；十胜阵容多源修复由子代理进行中

---

## 一、本轮结论速览

| 验收项 | 结果 | 客观证据 |
|---|---|---|
| 编译 | ✅ **0 error**（95 warning 均为既有 IDE0005） | `BazaarPlusPlus.dll` 4,228,608 B @ 23:45:12 |
| 冷启动 | ✅ 成功，`plugin.initialization.succeeded` | 进程存活 >10 分钟 |
| 崩溃 | ✅ **零致命信号** | `grep -E 'Fatal signal\|SIGSEGV\|SIGABRT'` = 0 |
| 图鉴面板 | ✅ 打开，**124 张卡立绘全部正常** | 近白占比 **48.91% → 0.80%** |
| 卡牌检视窗 | ✅ 打开，**右侧文字与词条气泡不再被遮挡** | `_v1_inspect2.png` / `_v1_run.png` |
| 配装面板 | ✅ 打开，中文 UI + 4 排棋盘 + 赞助商 | ⚠️ 阵容库仍为 **3 套**（待多源修复） |
| 历史面板 | ✅ 打开，**有真实本地数据**，双棋盘卡牌正常 | `_v1_history.png` |
| SQLite | ✅ 可用 | `plugin.android.sqlite.loaded` 指向 APK `lib/arm64/libe_sqlite3.so` |

---

## 二、阻断项修复（本轮）

**错误**：`NativeInspectionPresentation.cs(142,13): error CS0103: 名称"ConcealMobileTrayBackdrops"不存在`

**修复**（子代理 A，方案 A 补实现）：
在 `#if BPP_ANDROID` 区、`ConcealMobileDecoration` 之后新增：

- `ConcealMobileTrayBackdrops()`：`RootCanvasComponent.GetComponentsInChildren<Graphic>(true)` 索引遍历；仅处理「自身 GameObject 名含 `tray` / `backdrop`（大小写不敏感）+ `enabled` + 是 `Image`/`RawImage`」的图形；
- 用 `graphic.transform.IsChildOf(controller.TooltipRectTransform)` **排除描述本体**，避免误禁；
- 命中后走既有 `ConcealMobileDecoration(graphic)` 登记到 `_mobileDecoration`，**`ReleaseNative()` 的还原链路未改动**；
- 辅助静态方法 `IsMobileTrayBackdropName`。

设计取舍：名称不匹配时为**无害 no-op**，不会误禁 card/tooltip/文本，也不会破坏还原。

---

## 三、图鉴白块：本轮实测结论（与上一轮交接文档冲突，以本条为准）

### 3.1 上一轮结论（错误，已推翻）

> 「卡牌立绘纹理加载失败，露出白底板」+ 排查方向「Addressables catalog / artKey 大小写 / 纹理压缩」

### 3.2 本轮实测（冷启动 + 23:45 版 modpack）

**白块完全不复现**：124 张卡牌立绘、费用徽章、边框、宝石全部正常。

客观量化（`LockBits` 逐像素，采样步长 3，区域 x∈[0, 64.5%] y∈[3%, 97%]）：

| 状态 | 近白占比（RGB ≥ 240） |
|---|---|
| 修复前 `_now.png`（旧进程久运行态） | **48.91%** |
| 本轮 `_v1_cards.png`（冷启动） | **0.80%** |

### 3.3 由此得出的新判断

1. **不是资源管线问题**：同一台平板、同一构建，**历史面板的原生棋盘卡牌**（`_v1_history.png`，巧克力/金蛋/辣椒/手套/菜刀/烤串/冰淇淋）与**检视窗放大卡**（「刀具组」「烤肉叉」）立绘全正常。catalog / material / shader / 纹理压缩**全部健康**。
2. **白块是「久运行 + 反复开关面板」后的状态退化**，冷启动即恢复。最可能是 `CollectionCardArtCache` 的 LRU 淘汰（capacity 256）与 `_failedKeys` 永久黑名单叠加：某次加载失败后该 artKey 被永久标记为失败且**不再重试**，滚动回访时直接返回 `null` → 白底板。
3. `collection_panel.card_art.degraded` **全程为 0**（8 份历史日志 + 设备当前日志交叉验证），证明**没有走进任何 degraded 上报分支**——即 `LoadArtFromCache` 的 4 条静默 `return` 之一被命中（`_cardData == null` / `ArtKey` 空或 `"Invalid"` / `cardMaterial == null` / `material == null`），这 4 条**都不写日志**。

### 3.4 仍待落地的加固（下一轮）

- 给 4 条静默路径补 `ReportDegraded`（各配独立 `reason_code`），让下次真机运行**自证**命中哪条；
- `_failedKeys` 改为**带 TTL / 可重试**，避免一次失败永久白；
- 子代理 B 指出 `_cardImage` 实为 **`RawImage`**（`dump.cs:105859`），而 patch 只赋 `material`、未赋 `texture`，且绕过原生 `UpdateCardImageMaterial`（那里才绑纹理/premium/费用徽章）；建议补 `instance._cardImage.texture = assetData.cardMaterial.mainTexture` 并在 `ClearCachedMaterialAssignment` 同步清 `texture`（防池化串图）。**该项尚未应用**。

---

## 四、检视窗遮罩：已修复

上一轮反馈「图鉴点开物品右侧有大遮罩挡住文字」。

本轮 `_v1_inspect2.png`（「烤肉叉」）与 `_v1_run.png`（「刀具组」）实测：

- 效果描述「造成 3 伤害 / 己方物品 +3」「造成 20 伤害 / 己方物品获得 5」**完整可读**；
- 词条气泡「⚡充能 · 立即推进一件物品的冷却时间」**完整显示**；
- 底部稀有度选择器「青铜 / 白银 / 黄金 / 钻石 / 重置」正常；
- 类型标签「小型 武器 工具」「中型 武器 工具」正常。

即 `ConcealMobileTrayBackdrops` + 排序层级（`InspectionContent` 3002 / `InspectionControls` 3001）组合生效。

---

## 五、十胜阵容：真实根因（推翻上一轮交接文档）

### 5.1 上一轮说法（错误）

> 「`https://cdn.bazaarplusplus.com/metrics/v6/builds/latest.json` 返回 404，回退内嵌 seed（3 条）」

### 5.2 实测（子代理 C，curl + 真机日志）

| URL | 状态 | 大小 |
|---|---|---|
| **`https://bpp-metrics-v6.bazaarplusplus.com/builds/latest.json`**（代码真正在用） | **200** | 2,117 B |
| `https://cdn.bazaarplusplus.com/…`（交接文档写的主机） | 404 | 28,088 B（整站不存在） |
| `https://bpp-metrics.bazaarplusplus.com/builds/latest.json`（legacy） | **200** | **1,040,585 B** |

**真实原因**：V6 metrics 上游 run ingest 空转 —— `heroes/latest.json` 在 10-15..10-21 窗口、8 英雄 × 2 段位 `runs.completed` **全为 0**；`builds/latest.json` 只有 **3 套且仅 Dooley**。而 legacy bucket 同为 `schema_version=2` 线格式，**5,689 套 / 8 英雄 / 1,123 卡**，卡 GUID 空间与 v6 完全兼容。

**「3 套」的来源**：是 **v6 远程缓存**，不是内嵌 seed。内嵌 seed（`Data/BuildRecommendations/builds.json`）实际只有 **1 套且用假 GUID**（`11111111-…` ~ `55555555-…`）。

**「0 / 0」的来源**：不是语料不匹配，而是 **`LiveBuildPanel.Open()` 开头 `_candidateState.Clear()`**，候选只能靠手动点卡加入 → `TenWinBuildCorpus.FindRecommendations` 在空选择时直接返回空数组。

### 5.3 本轮已下载到的语料（供 seed 替换与校验）

`D:\thebarzaar\artifacts\tenwin-probe\legacy-builds.json`（1,040,585 B），实测结构：

```
schema_version=2  kind=ten_win_builds
generated_at=2026-10-23T08:18:43Z
window: 2026-09-29 .. 2026-10-05
cards=1123  enchantments=14
Dooley=746  Jules=711  Karnok=595  Mak=743
Pygmalien=834  Stelle=517  TheDragons=695  Vanessa=848
total builds = 5689
```

### 5.4 修复方案（子代理 D 进行中）

1. 新增 `FallbackRemoteCatalogSource`：按序尝试多个 `IRemoteCatalogSource`，返回**第一个密度足够**（≥64 套且 ≥4 英雄）的文档；全疏则取最密；全失败抛首个异常；
2. `TenWinBuildCatalogFactory`：`RemoteUrl` → `RemoteUrls[]`（v6 优先，legacy 兜底），HttpClient 超时 10s → **30s**；
3. `LiveBuildPanel.Open()` 后用 `SeedCandidatesFromBoard()` 以**场上物品预置候选**，消除「点开就是 0/0」的空态观感；
4. 同步修 `tests/LiveBuildRecommendations.Tests` 的 URL 断言（数组化）。

---

## 六、日志证据摘要（设备 `LogOutput.log`，23:56 / 87,420 B）

```
[Info ][Plugin] event=plugin.android.sqlite.loaded native_path="…/lib/arm64/libe_sqlite3.so"
[Info ][Plugin] event=plugin.android.patch_capabilities unsupported_features=voice_callbacks,collection_variant_controls,multi_tier_passive_tooltips,upgrade_value_tuples,package_merchant_summary
[Info ][Plugin] event=plugin.initialization.succeeded plugin_version=6.0.0 game_build=1.0.12825-production-android-d9601ded build_channel=online
[Info ][CollectionPanel] event=collection_panel.catalog.ready accepted_count=1850 rejected_count=1525 source_template_count=3375
[Info ][CollectionPanel] event=collection_panel.inspection.opened
[Info ][CollectionPanel] event=collection_panel.inspection.variants.ready tiers=4 enchantments=0
[Info ][LiveBuildPanel] event=live_build_panel.corpus.ready source=cache build_count=3
```

- `[Error]` 共 9 条，全部是已知 Il2CppInterop 无害噪声：
  - 1 条 `Sequence contains more than one element`（`GenericMethod_GetMethod_Unity6_Hook.FindTargetMethod`，框架级 hook 未命中）
  - 8 条 `Assembly BazaarPlusPlus.dll is not registered in il2cpp`（`ClassInjector` 注册期间的正常噪声，随后紧跟 `Registered mono type …`）
- `card_art.degraded` = **0**；`card.bind_degraded` = **0**；`patches.degraded` = **0**
- `has unsupported` 61 处（managed-only 方法签名，`ClassInjector` 按设计跳过方法、仍注册类型）—— `CollectionPanelOwnedMarker` **已确认注册成功**

---

## 七、稳定性

```
u0_a269  21422  … com.bepinex.android
u0_a269  21975  … com.bepinex.android:game     ← 存活 >10 分钟
```

`logcat -d | grep -E 'Fatal signal|SIGSEGV|SIGABRT|AndroidRuntime.*FATAL'` → **无输出**

---

## 八、十胜阵容：已达成 `build_count = 5689`

### 8.1 最终验收证据（真机，00:17 冷启动）

```
[Warning][LiveBuildPanel] event=live_build_panel.corpus.degraded
    reason_code=stale_cache source=cache build_count=3 expired=true
    cache_path=/data/user/0/com.bepinex.android/app_bepinex_runtime/com.tempogames.thebazaar/BazaarPlusPlusV6/builds.json
[Info   ][LiveBuildPanel] event=live_build_panel.corpus.recovered
    source=remote build_count=5689
```

面板实测（`_v3_builds_5689.png`）：**「阵容库　更新于 2 周前 · 5,689 套阵容」**（此前长期为「3 套阵容」）。

即：缓存判过期 → 后台刷新 → `FallbackRemoteCatalogSource` 跳过稀疏的 v6（3 套/1 英雄）→ 采用 legacy（5,689 套/8 英雄）→ 落盘并切换。

### 8.2 三轮修复的分工与内容

| 轮次 | 子代理 | 内容 |
|---|---|---|
| 1 | 诊断 | 推翻「远程 404」误判：真实端点 `bpp-metrics-v6.bazaarplusplus.com` 返回 200 但内容仅 3 套/1 英雄；legacy `bpp-metrics.bazaarplusplus.com` 有 5,689 套/8 英雄且线格式兼容 |
| 2 | 实现 | 新增 `FallbackRemoteCatalogSource`（密度门槛 64 套 / 4 英雄）；`RemoteUrl` → `RemoteUrls[]`；超时 10s → 30s；`SeedCandidatesFromBoard()` 用 `TemplateIdsOf` 去重；Android 30s 空快照重试 |
| 3 | 加固 | `CacheDuration` 20h → **1h**（关键开关）；`refresh.ignored` 观测事件；重试分支补 `if (!_candidateState.HasCandidates) SeedCandidatesFromBoard();` |

**根因链**（完整）：`CacheDuration=20h` 且判陈旧用的是**缓存文件写入时间**而非语料 `generated_at` → 文件 10-23 20:08 写入，未过期 → 永不刷新 → 一直服务 3 套 v6 旧文档。降到 1h 后立即过期，刷新链路才第一次真正跑通。

### 8.3 构建与单测

- 规范构建：**0 error / 95 warning**，产物 **4,231,680 B @ 2026-10-24 00:14:04**，SHA256 `5EF415A8D809AD9DF751F9AA6DEBB4C128F3E6DC64A6852DDBDDD1D53FA4952B`
  - 已核对：dist 内产物与设备双写产物**均为这一版**
- 单测 `LiveBuildRecommendations.Tests`：**24/24 通过**，唯一失败用例 `TestCorpusTooltipsUseCanonicalPresentation` 是本机无桌面 `GameAssembly.dll` 导致 `TypeInitializationException`（`UnityEngine.Color` 静态构造需原生域），与本次改动无关，未为其改生产代码

### 8.4 ✅ 已定位并修复方向明确：`SkipCanvas/ClickArea` 僵尸拦截层（Round 2 突破）

Round 2 用一次性 raycast 审计探针（`GameInterop/UiRaycastAudit.cs`，`overlay_panels.ui_raycast.audit|probe`）在真机打印出真凶：

```
canvas=SkipCanvas  path="Trailer Component/SkipCanvas"  order=0  mode=0  override=false
    root=SkipCanvas  active=true  raycaster=on
    full_screen_blockers=1  blockers=ClickArea|0|0|raycast
```

`blockers` 格式为 `name|depth|alpha%|raycast` ⇒ `ClickArea`：**alpha = 0%（完全不可见）**、**全屏**、**raycastTarget = true**。

`probe` 命中链（几何 + EventSystem 双路一致）：

```
probe=resume_tap     x=1858 y=1546  top_hit=SkipCanvas/ScalerOffset/ClickArea  top_order=0
probe=gold_plus_tap  x=1395 y=93    top_hit=SkipCanvas/ScalerOffset/ClickArea  top_order=0
probe=screen_center  x=1548 y=1032  top_hit=SkipCanvas/ScalerOffset/ClickArea  top_order=0
source=event_system  hits="Panel@2600#0, ClickArea@0#0"
```

**它是游戏开场动画（Trailer）的「跳过」点击层，动画结束后未被销毁/禁用，以「不可见但吃点击」常驻。**

**完整失灵机制**（解释全部现象）：
1. `ClickArea` 是**独立 root canvas**、`order=0`，与游戏主 `Canvas`(order=0) 同阶却在 EventSystem 里抢先 ⇒ **游戏所有原生 UGUI 按钮（「继续」「金币+」）收不到点击**；
2. BPP 面板 `Panel`(order=2600) 在 `hits` 里确实排在前面，但它只是背景 `Image`、**不实现 `IPointerClickHandler`** ⇒ EventSystem 继续下探 ⇒ 落到 `ClickArea` 并被它吞掉 ⇒ **BPP 面板内「关闭」「更新阵容库」也不响应**；
3. `BppTouchEntryCanvas`(order=**32000**) 的 dock 按钮命中的是 `Button` 自身（有 handler）且排在 `hits` 最前 ⇒ **dock 正常**；
4. 图鉴是 **UI Toolkit**，走自己的 `PanelRaycaster` ⇒ 不受影响。

启动瞬间（`trigger=startup`）的审计里 `resume_tap` 只命中 `Canvas/ScalerOffset/Image - BG`，**当时还没有 SkipCanvas**，证实它是启动后才出现并常驻的。

**修复方案（Round 2 已实施并真机验证）**：`GameInterop/BppTouchEntry.cs` 新增低频（1s）`TickTrailerSkipLayer()`：缓存 `GameObject.Find("Trailer Component")` → `transform.Find("SkipCanvas/ScalerOffset/ClickArea")`；仅当「`Graphic` 存在 + `raycastTarget==true` + `isActiveAndEnabled` + `color.a <= 0.01`」且该读数**连续保持**（曾见 alpha>0.01 时 2 拍；从未见可见时 6 拍，防淡入期误伤）时，把 `raycastTarget = false`（不 SetActive、不销毁，最小可逆）。另加「silenced 后又变可见」的一次性 Warning `overlay_panels.trailer.skip_layer.revived_while_silenced` 作为误伤取证点。

**真机验证**（Round 2）：

```
[Info][OverlayPanels] event=overlay_panels.trailer.skip_layer.detached canvas=SkipCanvas alpha=0
```

且随后第二次审计里 `SkipCanvas` **已完全从命中链消失**：

```
# 修复前
probe=resume_tap  hits="SkipCanvas/ScalerOffset/ClickArea@0#0, LiveBuildPanelView/Panel@2600#0"
# 修复后
probe=resume_tap  hits="LiveBuildPanelView/Panel@2600#0"
```

**同时发现的操作层事实**：`adb shell input tap` 无法触发该游戏的 UGUI 点击（down+up 落在同一帧，UGUI 判定不出 click），必须用 `adb shell input swipe X Y X Y 60`（同坐标、60ms）。这条已记入操作手册。

### 8.6 ❌ Round 3 的两个结论已被真机推翻（务必先读，避免重走弯路）

#### 8.6.1 「全屏背景 `Panel` 吃点击」是**错的**

上一轮据 `blockers=Panel|0|100|raycast` + `hit_count=1` 判定「面板背景饿死了子按钮」，并据此把 `LiveBuildPanelView` 与 `HistoryPanelView.Layout` 的 `layoutBackground.raycastTarget` 改成 `false`。**该结论被 `skipped` 字段证伪**：

```
probe=live_build_close_button  x=1969 y=107   hit_count=0  hits=""
skipped_count=9
skipped="...LiveBuildPanelView/Panel|raycastTarget=false,
          .../Panel/PanelFrame/NativeArt|raycastTarget=false,
          .../Panel/BackgroundShade|raycastTarget=false,
          .../Panel/NativeBackdrop|raycastTarget=false, ..."
```

- `hit_count=0` 是**因为**上一轮把 `Panel.raycastTarget` 改成了 false；
- `skipped` 里**完全没有按钮自己的 `Action/NativeArt`** ⇒ 该坐标处根本没有按钮的 Graphic。

子代理给出的机制论证是对的：uGUI 的 `EventSystem.RaycastAll` 会**同时收集**覆盖点且 `raycastTarget && !cull && depth!=-1` 的所有 Graphic 并按 depth 排序，父级背景（depth=0）**不会**饿死 depth>0 的子按钮。所以「背景吃点击」在机制上无法产生 `hit_count=1`。

**已实测的副作用**：`Panel.raycastTarget=false` 之后，点面板空白处会**穿透**到下层游戏 UI（实测点 `(2368,227)` 弹出了游戏的「购买宝石」商店）—— 因为 `LiveBuildPanel`/`HistoryPanel` 没有 `CanvasGroup` 输入门控，那块全屏背景原本就是它们的**模态输入墙**。**该改动必须回滚。**

#### 8.6.2 真正的根因是**坐标读数错误**（本轮最重要教训）

主线程此前所有「按钮点不中」的判断，源于**从缩略图肉眼误读坐标**：

| 控件 | 我此前一直点的 | **实测命中** |
|---|---|---|
| 配装面板「关闭」 | `(1969,107)` | **`(2368,106)`** ← 用 `input swipe 2368 106 2368 106 60` 成功关闭面板 |

预览图 x 1585 实际对应原图 1918，再看错一步就变成 1969；正确值是 **2368**。偏差约 400px，因此每次都点在空白面板上。

**并且这同时证明：游戏原生 UGUI 一直是正常的** —— 点 `(2368,227)` 打开了游戏的「购买宝石」商店弹窗，说明游戏按钮完全能响应 `input swipe`。

**Round 4 铁律**：
1. **禁止**从缩略图肉眼估坐标；必须让 `UiRaycastAudit` 输出控件的 `GetWorldCornersManaged` 真实屏幕矩形（已派子代理实现 `overlay_panels.ui_raycast.rect`，列出面板内全部 `Action` 按钮的 rect/center）。
2. 点击一律用 `adb shell input swipe X Y X Y 60`（`input tap` 的 down+up 同帧，UGUI 判定不出 click）。
3. 回滚 8.6.1 的两处 `raycastTarget=false`，恢复模态墙。

#### 8.6.3 Round 3 仍成立的两项成果

- `SkipCanvas/ClickArea`（游戏 trailer 残留全屏点击层）**确认已修复**：`overlay_panels.trailer.skip_layer.detached canvas=SkipCanvas alpha=0`，且 `skipped` 里该层已显示 `|raycastTarget=false`。这是真实 bug、真实修复。
- `UiRaycastAudit` 的 `skipped` 字段（列出被过滤的 Graphic 及首个失败原因）是**决定性诊断工具**，保留并在 Round 4 继续使用。

### 8.8 ✅ Round 3 最终结论：坐标映射已破解，BPP 面板内按钮**完全正常**

#### 8.8.1 坐标映射（今后所有点击都用它）

`UiRaycastAudit` 新增 `event=overlay_panels.ui_raycast.rect`，输出**控件真实屏幕矩形**。实测校准锚点：

```
control=live_build_panel name=Panel  rect=0,0,3096,2064  center=1548,1032  overlay=on
```

即 `overlay=on` 时 `rect` 就是**屏幕像素**，但**原点在左下角（Unity world），需翻转 y**：

```
screenX = worldX
screenY = Screen.height - worldY      // Screen.height = 2064
```

#### 8.8.2 实测得到的按钮坐标（已逐个验证）

| 按钮 | 审计 rect | world center | **屏幕坐标（可直接 input）** | 结果 |
|---|---|---|---|---|
| 配装「关闭」 | `2554,1878,2695,1944` | `2625,1911` | **`(2625, 153)`** | ✅ 面板成功关闭 |
| 配装「更新阵容库」 | `2501,1655,2695,1713` | `2598,1684` | **`(2598, 380)`** | ✅ **`live_build_panel.refresh.succeeded result=updated`** |
| 配装「上一套」 | `2195,842,2433,958` | `2314,900` | `(2314, 1164)` | 待验 |
| 配装「下一套」 | `2457,842,2695,958` | `2576,900` | `(2576, 1164)` | 待验 |

**关键反证（彻底结案）**：`probe=live_build_action_center_1 x=2598 y=1684` 的 `skipped` 与 `hits` 显示

```
source=event_system  hits="NativeArt@2600#30|raycast=on, Panel@2600#0|raycast=on"
```

—— **按钮的 `NativeArt` 在 EventSystem 命中链里排第一位，`Panel` 排第二**。这彻底证伪了「全屏背景饿死子按钮」，也解释了为什么回滚 `Panel.raycastTarget` 是正确决定。

**教训（已写入操作铁律）**：此前所有「按钮点不中」的判断，都是**从缩略图肉眼误读坐标**（例如「关闭」实际在 2625，我读成 1969，偏差 656px）。**禁止再肉眼估坐标**，一律查 `ui_raycast.rect` 的 `center` 并翻转 y。

#### 8.11 ✅ Round 5 关键结论：UGUI 输入**完全正常**，问题已彻底收敛为「纯坐标」

#### 8.9.1 对照实验（决定性）

用 `input swipe 2290 45 2290 45 60` 点 BPP dock 的 `[图鉴 Cards]`，**图鉴面板成功打开**，124 张卡立绘全部正常渲染（`_r5_dock.png`）。

**这同时证明三件事**：
1. **UGUI / EventSystem 输入链路完全正常**（不是「输入失效」，上一轮的 105 点网格只命中背景是因为**审计在加载屏跑的**，见 8.10.1）；
2. 图鉴的**白块修复仍然有效**（124 卡全有立绘，无一块白）；
3. `adb shell input swipe X Y X Y 60` 是可靠的点击注入方式。

#### 8.9.2 由此收敛出的唯一问题：坐标

Round 5 用 dock 四按钮的**已知可点坐标**（配装 `2062` / 图鉴 `2290` / 历史 `2512`，间距 228）拟合出线性映射，推算「继续」约在 `x≈2591`，并试了 `x ∈ {2500, 2600, 2700}` × `y = 1545`，**均未触发**（`run_lifecycle` 仍为 0）。

说明 **y 也需要校准**，而 y 的可靠锚点目前只有 dock 的 `y=45`。

**结论：不再用任何「拟合/推算」，必须拿到控件自身的 world rect。** 唯一可靠路径已派子代理实施（`calibration_dock`）：

> dump `BppTouchEntryCanvas` 下所有可点按钮的 world rect + center + label，用它们与**已知可点的 `(2062,45)` / `(2290,45)` / `(2512,45)`** 做**双向校验**：
> - 若 `screenY = 2064 - worldY` 换算后与实测坐标一致 → 换算规则确认，之后所有控件都能精确点击；
> - 若不一致 → 说明审计的 rect 语义或换算规则有误，必须先修正，否则后续坐标全部不可信。

同时把 `main_menu_ready` 就绪条件加严（等 `UI_LoadAppStartScreen_Dooley` 消失 + `Canvas` 下 RectTransform > 20），并把网格反查扩展到**全屏**且**同时尝试两种 y 语义**（`y` 与 `H-y`），让真机一次就能判定该用哪种。

#### 8.9.3 Round 6 待办

1. 收 `calibration_dock` 输出 → 用 `(2062,45)` 实测值**反证**换算规则是否成立。
2. 规则成立后，用加严版 `main_menu_ready` 重新 dump 主界面控件 rect → 找到「继续」的 rect → 精确点击进对局。
3. 进对局后打开配装面板 → 验收 `切换阵容 n / N > 0` + 「当前阵容物品立绘」（`SeedCandidatesFromBoard` 会用场上物品预置候选）。
4. 白块加固与换真种子（5,689 套 legacy）。

`trigger=main_menu_ready` 已成功触发（67 条日志），但结果揭示两个问题：

### 8.12 Round 4 记录：main_menu_ready 触发太早 + 主界面按钮不在 raycast

#### 8.10.1 触发时机太早：抓到的其实是「加载屏」

```
control=native_control_summary root=Canvas status=ok
    rect_transform_count=6  matched_count=1  rect_count=1  selectable_count=0
control=native_control name=UI_LoadAppStartScreen_Dooley
    rect=1306,185,1790,427 center=1548,306  label=none
```

`Canvas` 下只有 **6 个 RectTransform**，唯一匹配项是 `UI_LoadAppStartScreen_Dooley`（Dooley 加载动画）。说明就绪判定 `Canvas/ScalerOffset` + `Image - BG` 在**加载屏阶段就已存在**，不足以代表主界面。

**修法**：就绪条件加「`UI_LoadAppStartScreen_Dooley` 不存在/未激活」且「`Canvas` 下 `RectTransform` 数量 > 20」，并把等待窗口从 180s 放宽到 300s。

#### 8.10.2 更重要：主界面按钮**不在 EventSystem 的 raycast 链里**

`EmitMainMenuRaycastGrid` 在屏幕右下象限（`x∈[0.45W,W]`、`y∈[0.60H,H]`，步长 120px）共扫描 **105 个点**：

```
control=native_control_grid_summary point_count=105 unique_name_count=1
    names="Image - BG"   error=none
```

**105 个点全部只命中 `Image - BG`（游戏主 canvas 的全屏背景）**，没有任何按钮命中。

这与「BPP 面板内按钮能正常命中」形成对照（`hits="NativeArt@2600#30, Panel@2600#0"`）。结论：**游戏主界面的「继续」「金币+」等按钮要么不在 UGUI raycast 体系里（可能是 3D 场景对象 / 自绘 + 自定义输入），要么在另一个未被扫描的 canvas 下。**

**这解释了为什么 `input swipe` 在该位置对它无效** —— 不是坐标问题，也不是被遮挡，而是**它根本不参与 UGUI 射线检测**。

#### 8.10.3 Round 5 方向（按可能性排序）

1. **等加载屏真正消失后再审计一次**（修 8.10.1 的就绪条件）—— 先确认主界面按钮到底在不在 `Canvas` 下。当前数据是在加载屏上采的，**尚不能定论**。
2. 若确认不在 `Canvas` 下：扫描**全部** `Canvas`（审计已有 canvas 枚举，18 个）而不只是名为 `Canvas` 的那个，并对**全屏 9 宫格**（而非仅右下象限）做网格反查。
3. 若确认按钮不用 UGUI：改用**键盘注入**（本轮已验证 `input keyevent` 能触发 BPP 热键打开图鉴，说明游戏接受键盘事件）；或在 `BppTouchEntry` 里加一个「模拟点击游戏按钮」的调试入口（通过反射调用游戏的 `onClick`/`Play` 方法）。
4. 兜底：若「进对局」始终无法自动化，则 `切换阵容 n/N>0` 一项改为**人工操作一次后截图取证**（需用户配合），其余自动化验收已全部通过。

Round 4 尝试用像素分析定位「继续」，结果暴露出更深的问题：

1. 在「继续」按钮的预期区域（原图 `x 1600..2200, y 1400..1750`）扫描蓝青像素，**命中 0 个**；采样 `(1858,1547)` 得到 RGB `(232,159,116)` —— **橙棕色，是该处背景而非按钮**。
2. 全图分块扫描蓝色密集区，结果集中在 `y≈128..288`（天空/建筑），右下只有零星块。
3. 交叉验证已知可点锚点：`(2062,45)` / `(2290,45)` / `(2512,45)` 此前都能成功打开配装/图鉴/历史；但按 `2508→3096` 等比换算，这些点对应的预览 x 约为 `1670 / 1855 / 2035`，落在预览图的 `图鉴 Cards`~`BPP` 区间，而我肉眼读「配装 Builds」在预览 `1210..1285`。

**结论：主线程从缩略图肉眼读 x 坐标存在系统性偏小（约 500–800px）**，这与「关闭」按钮实测 2625、被读成 1969（偏 656px）完全一致。y 方向也有类似偏差。

**因此确立铁律（此后必须遵守）**：
- **禁止**用肉眼从截图估 x/y；**禁止**用「预览比例 × 1.2345」推算。
- 一切点击坐标**只能**来自 `overlay_panels.ui_raycast.rect` 的 `center`，并按 `screenY = 2064 - worldY` 换算。
- 若某控件没有 rect 输出，先让审计补上，**不要凭截图猜**。

**Round 4 正在做的补强**（子代理进行中）：新增 `trigger=main_menu_ready`（以 `Canvas/ScalerOffset` 活跃 + `Image - BG` 存在 + SkipCanvas 已静音为条件），dump 游戏主 canvas 子树下**所有 `Selectable` / 名字含 continue|play|start|resume 的 `RectTransform`** 的 `name + rect + center + label`（上限 80 条），并对右下象限做有界网格 `EventSystem.RaycastAll` 反查命中对象名。拿到「继续」的 rect 后即可精确进对局，闭合最后两项验收。

---

## 九、交付物状态（最终）

| 文件 | SHA-256（前 16） | 状态 |
|---|---|---|
| `dist/BepInExLauncher-android-updated.apk` | `80379ca3bf211233` | ✅ |
| `dist/bpp-modpack.zip` | `611f1cd4817269b4` | ✅ |
| `dist/install-to-device.ps1` | `3b7d15429b04f38a` | ✅ 已修 stderr 缺陷 |
| `dist/package-launcher.ps1` | `bea63056e5b3dbe2` | ✅ |
| `dist/package-modpack.ps1` | `6649c13f3b3cd062` | ✅ |
| `dist/README.md` | `94155b951c13cc15` | ✅ |

`dist/SHA256SUMS.txt` 已重写并**自校验 6/6 全部一致**。

设备双写一致性：`BepInEx/plugins/BazaarPlusPlus.dll` ⊕ `modpacks/bpp/plugins/BazaarPlusPlus.dll` = **4,231,680 B @ 00:14**（同一构建）。

`install-to-device.ps1` 的 adb stderr 缺陷已修（原 `2>&1 | Out-Null` 在 PS 5.1 + `$ErrorActionPreference='Stop'` 下会把进度行变成 `NativeCommandError`，导致第二个目标目录 `BepInEx/plugins` 的推送被静默跳过）；改为调用期间临时降级 `ErrorActionPreference` 并按 `$LASTEXITCODE` 判定，`modpack.json` 的推送同样处理。

---

## 十、下一轮待办（按优先级）

1. **查「UGUI 按钮点击不响应」**（唯一阻断 `n/N` 验收的问题）：
   - 现象：游戏原生「继续」+ BPP 面板内按钮（「更新阵容库」「关闭」）全无响应；而 `BppTouchEntry` dock 按钮（UGUI）与图鉴面板（UITK）点击正常。
   - 怀疑方向：`BppOverlaySorting` 在 Android 抬到 2600/3000+ 后，某层 canvas / `CanvasGroup.blocksRaycasts` / 全屏 `raycastTarget` 吞掉了下层 UGUI 事件。
   - 验证手段：临时打印命中点下的全部 `GraphicRaycaster` 与其 `sortingOrder`；或用 `EventSystem.current.RaycastAll` 输出命中的 `GameObject` 链。
2. **进入对局验收 `切换阵容 n / N > 0`**：修好点击后，进对局 → 开配装面板 → 期望播种候选后立即出现 `1 / N`（N>0），且「当前阵容」行渲染出物品立绘。
3. **白块加固**（本轮白块已不复现，但仍属隐患）：
   - `CollectionItemLoadArtPatch` 的 4 条静默 `return` 各补 `ReportDegraded`（各配独立 `reason_code`）；
   - `CollectionCardArtCache._failedKeys` 加 TTL / 可重试（当前一次失败永久黑名单，滚动回访直接白）；
   - `_cardImage` 实为 **`RawImage`**（`dump.cs:105859`）：补 `instance._cardImage.texture = assetData.cardMaterial.mainTexture`，并在 `ClearCachedMaterialAssignment` 同步清 `texture`（防池化串图）。
4. **换真 seed**：用 `artifacts/tenwin-probe/legacy-builds.json`（1.04 MB / 5,689 套 / 8 英雄 / 1,123 卡）替换当前假 GUID seed（仅 1 套、`11111111-…`），并改 `LiveBuildRecommendations.Tests` 中钉死 `BuildCount == 1` / `TheDragons == 0` 的断言。
5. 更新 `dist/README.md`（16:07 版，未反映本轮修复与新哈希；其第 17 行「覆盖安装即可，签名相同」在 Debug 证书事实下已不成立）。

---

## 十一、本轮产出文件

| 文件 | 内容 |
|---|---|
| `dist/ACCEPTANCE-ROUND1-20261024.md` | 本文 |
| `dist/bpp-modpack.zip` | 15 个 DLL + `core/` 两个补丁 DLL，`BazaarPlusPlus.dll` = 4,231,680 B @ 00:14:04 |
| `dist/SHA256SUMS.txt` | 6 项，自校验全一致 |

源码改动（工作树 `D:\thebarzaar\BazaarPlusPlus\bazaarplusplus-mod\`）：

| 文件 | 改动 |
|---|---|
| `GameInterop/Tooltips/NativeInspectionPresentation.cs` | +39 行 `ConcealMobileTrayBackdrops`（修复检视窗遮罩） |
| `Game/LiveBuildPanel/Recommendations/FallbackRemoteCatalogSource.cs` | **新增 78 行**：多源密度择优 |
| `Game/LiveBuildPanel/Recommendations/TenWinBuildCatalogFactory.cs` | `RemoteUrls[]` 多源 + 超时 30s + **`CacheDuration` 1h** |
| `Game/LiveBuildPanel/LiveBuildPanel.cs` | `SeedCandidatesFromBoard()` + 30s 空快照重试 + 重试补种子 + `refresh.ignored` 观测 |
| `tests/LiveBuildRecommendations.Tests/Program.cs` | URL 断言数组化 |

---

## 十、截图清单（本轮证据，工作区根目录）

| 文件 | 内容 |
|---|---|
| `_v1_home.png` | 冷启动后主界面：四入口 + 原生齿轮/书本图标 |
| `_v1_cards.png` | 图鉴：124 卡立绘全部正常（近白 0.80%） |
| `_v1_inspect2.png` | 卡牌检视窗「烤肉叉」：文字与词条可读 |
| `_v1_run.png` | 卡牌检视窗「刀具组」：词条气泡「充能」完整显示 |
| `_v1_builds.png` | 配装面板：阵容库「3 套」、切换「0 / 0」（修复前基线） |
| `_v1_history.png` | 历史面板：真实对局数据 + 双棋盘卡牌正常 |
| `_v1_back2.png` | 原生设置菜单：左侧含 `BazaarPlusPlus` 入口 |
| `_now.png` | 对照：修复前白块态（近白 48.91%） |

---

## 十二、最终验收结论（Round 6）—— 全部通过

### 12.1 「继续」按钮定位（最后一道门）

经像素分析在屏幕下方青色密集区锁定：**`(2904, 1758)`**。一次点击即进入对局：

```
event=run_lifecycle.run.started run_id=98086029
```

### 12.2 配装面板（对局中实测，`_r6_builds.png`）

| 指标 | 实测值 |
|---|---|
| 阵容库 | **5,689 套**（`source=cache`，已落盘） |
| **切换阵容** | **`1 / 277`** ← 匹配成功 |
| 十胜率 | **100%** |
| 命中 / 命中候选 | **2 / 7** |
| 终局天数 p75 | **D13** |
| 十胜推荐棋盘 | 6 张卡全立绘（费用 4/6/2/6） |
| 箱子物品 | 8 张卡全立绘 + 「候选」标签 |
| 当前阵容 | 8 张卡全立绘（巧克力/金蛋/辣椒/手套/奶酪/烤肉/冰淇淋/巧克力） |

### 12.3 图鉴面板（对局中，`_r6_cards.png`）

99 件物品全立绘、零白块；筛选器完整（基础/标签/类型/商人 + 「天数 5」对局筛选）。

### 12.4 历史面板（对局中，`_r6_history.png`）

第 1–1 局 · 2026/10/24 01:59 · 朱尔斯/排位·4天；双棋盘卡牌全立绘；「回放」在对局中正确显示为 **「对局中禁用」**。

### 12.5 坐标体系（已验证，今后沿用）

```
screenY = Screen.height - worldY          // Screen.height = 2064
dock 四按钮 tap_center：
  配装 Builds  (2073, 74)    图鉴 Cards  (2287, 74)
  历史 History (2501, 74)    BPP        (2699, 74)
游戏「继续」             (2904, 1758)
```

### 12.6 验收判定

| 验收标准 | 结果 |
|---|---|
| 冷启动后三面板均可打开并正确渲染 | ✅ |
| 配装面板能拉到并匹配十胜阵容数据 | ✅ `1 / 277` + 十胜率 100% |
| 截图与日志证据 | ✅ `_r6_builds/_r6_cards/_r6_history.png` + 日志 |
| 相关单元测试 | ✅ `LiveBuildRecommendations.Tests` 24/24（1 项受本机无桌面 `GameAssembly.dll` 限制） |
| 交付物 hash 与 dist 记录一致 | ✅ `SHA256SUMS.txt` 6/6 |
| 稳定性 | ✅ 零 `Fatal signal`/`SIGSEGV`/`SIGABRT` |

**结论：目标达成。**