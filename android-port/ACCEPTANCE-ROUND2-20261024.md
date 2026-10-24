# BazaarPlusPlus 安卓移植 — 第 2 轮验收记录（2026-10-24）

> 设备：vivo PA2573 平板，Android 16，无线调试 `192.168.0.14:37699`
> 游戏：`com.tempogames.thebazaar` 1.0.12825-production-android-d9601ded
> 启动器：`com.bepinex.android`（BepInEx Launcher，modpack `bpp` 15/15）
> 截图证据：工作区根目录 `_r8_*` / `_r9_*` / `_r10_*`
> 日志证据：`_r8_LogOutput.log`（756 行）

---

## 一、本轮结论速览

| 项 | 结果 | 证据 |
|---|---|---|
| 图鉴方案 A（左半屏放大卡 + 右半屏原生抽屉） | ✅ 落地并推送 `dcd7189` | `_r8_inspect.png` |
| 抽屉不透明底板（本轮新增修复） | ✅ 筛选栏不再透出 | 修复前 `_r7_inspect2.png` / 修复后 `_r8_inspect.png` |
| 回放终态 `playback.succeeded` | ✅ 缺口闭合（此前误判为日志机制坏） | 165,101 ms / 250,932 ms，两次 0 降级 |
| 二次进入回放 `playback.started` | ✅ 未复现，两次都正常 | 同一会话连续两次回放 |
| 白块加固三项 | ✅ 已落地、已部署、`card_art.degraded=0` | `_r10_cards.png` |
| 十胜阵容 | ✅ 5,689 套 | `_r8_builds.png` |
| SQLite | ✅ `plugin.android.sqlite.loaded` | `_r8_LogOutput.log` |
| 字体降级 | ✅ 已修：冷启动后 `native_game_fonts.degraded` 完全消失 | `_r11` 启动日志 |
| 商家包摘要补丁 | 🔄 已恢复（移除 `Prepare()=>false`），待对局内包裹验证 | 见 §六 |

---

## 二、图鉴：方案 A + 不透明底板

### 2.1 方案 A 的落地形态（提交 `dcd7189`）

- 左半屏：放大卡 + 原生品质/附魔切换条（`inspection.variants.ready tiers=4`）
- 右半屏：游戏自带的 Mobile Lock Mode 抽屉（`Scroll View`），保留 `CanvasScaler` / viewport mask / 滚动布局
- 真机日志：`collection_panel.inspection.android_drawer render_mode=ScreenSpaceOverlay sorting_order=3002 left=2111 width=985`

### 2.2 本轮发现并修复的缺陷：抽屉背景透明

**现象**：抽屉文字与背后图鉴筛选栏完全重叠、不可读。

**根因**：原生抽屉自身没有不透明底板；其 `lockModeBackgroundImage` 被 BPP 禁用（否则会压暗左半屏放大卡），只剩 53% 黑色 dimmer。

**修复**：`NativeInspectionPresentation` 在 dimmer 之上新增 `DrawerBackdrop`，用 `Colors.CollectionPanelBackground` 精确填满 `Scroll View` 的屏幕矩形（`left=2111 width=985`），尺寸随 tray 实时刷新，`raycastTarget=false`，桌面分支零改动。

### 2.3 验收点

- 抽屉文字清晰、筛选栏不透出（放大 2× 复核：灰白字 + 深棕底，边界干净）
- 品质切换：点「钻石」后卡片边框与描述同步更新
- 关闭检视：图鉴完全还原，无残留底板、无白块

---

## 三、回放：终态缺口闭合（重要更正）

上一轮交接文档称“`playback.succeeded/degraded/failed` 全为 0，缺口未闭合”。本轮实测**推翻该结论**：

```
playback.started          battle_id=00f38840 source=local_saved record_video=false
playback.ended_observed   native_recording=false managed_recording=false recap_action_bound=true handled=false
playback.succeeded        battle_id=00f38840 source=local_saved end_reason_code=saved_replay_exit
                          duration_ms=165101 reason_code=none degradation_count=0 rollback_status=not_required
```

**结论**：终态事件由 `ReplayState` 退出驱动，必须等回放走到 recap 并点「继续」才会发出。上一轮验收停在“回放播完”没点继续，因此只看到 `started`。日志机制没有问题。

第二次进入回放同样完整：`started → succeeded`，`duration_ms=250932`。

---

## 四、白块加固（ACCEPTANCE-ROUND1 §十.3 的三项）

| 项 | 改动 | 结果 |
|---|---|---|
| 4 条静默 return 补降级上报 | `CardDataMissing` / `ArtKeyMissing` / `ArtAssetMissing` / `CardMaterialMissing` / `MaterialMissing` / `CardTextureMissing` | 已部署，真机 `degraded=0` |
| `_failedKeys` 永久黑名单 | 改为 60 s TTL + `ClearFailedKeys()` | 已落地 |
| `RawImage.texture` | 补赋值 + 清空时同步清 texture | 已落地 |

独立红队审查结论：**无阻塞**，另有 7 项应修（`ClearFailedKeys` 零调用、两个失败出口未清纹理、池回收未清引用、`artTexture` 先赋值后判空等），已转修复任务。

---

## 五、字体降级：启动时序竞态误报

- 证据：`plugin.native_game_fonts.degraded stage=resolve_source_font reason_code=source_font_unavailable` 仅出现 1 次，随后 `recovered font_count=3`，全程无第二条。
- 根因：游戏侧先发布 `_configuration`（`HasConfiguration` 只判它非空），再加载两个 primary，最后才 `_initialized=true`；BPP 在这个窗口里发起解析，命中 `primary == null`。
- 资源本身完好：Addressables 按 RuntimeKey 加载（无大小写问题），APK 内 zh-CN 字体 3+3 齐全。
- 潜在风险：窗口内 `PrepareOwnedText` 会退回 `TMP_Settings.defaultFontAsset` 且不再重试，存在豆腐块风险。
- 已修：上报加 `AnyPrimaryPublished()` 前置条件、`HasConfiguration && 两 primary 皆空` 时返回 `Waiting`、degraded 字段补 `sans_primary/serif_primary/role`。
- **真机确认（2026-10-24 11:2x 冷启动）**：`LogOutput.log` 中 `native_game_fonts` 相关事件**一条都没有**（degraded/recovered/loaded 全部消失），`plugin.initialization.succeeded` 正常。

---

## 六、Harmony 5 项 `unsupported_features` 评估

| 特性 | 结论 |
|---|---|
| `package_merchant_summary` | **已修**：`__result` 改为 `Il2CppSystem.ValueTuple<StringBuilder, Nullable<TooltipSegment>>` 并按值原地 append；能力清单已移除该项（真机 `patch_capabilities` 确认）。待对局内包裹验证 `tooltips.package_merchant_summary.applied` |
| `multi_tier_passive_tooltips` | **可修**（同一根因、同一方法） |
| `voice_callbacks` | transpiler 路径不可修；可改走 eventPath 元数据，需真机探针 |
| `upgrade_value_tuples` | 16 字节内联返回 ABI，当前钩子不可修，只能换钩子 |
| `collection_variant_controls` | 清单已过时：能力已由 BPP 自绘变体栏替代 |

注：`Plugin.cs:422` 是手写字符串清单，不做门控，只影响日志准确性。

---

## 七、操作要点（本轮新增）

- 历史面板底部按钮的**命中区与视觉框不一致**：「回放」实际中心在 `x≈2590`，按文字中心 `2166` 点击会落在「录制」与「回放」之间的空隙，**没有任何按下反馈**，容易误判为按钮失效。
- dock 坐标（实测可用）：配装 `2028,47`、图鉴 `2253,47`、历史 `2452,47`。
- 图鉴关闭 X：`2985,96`。
- 设备 `wm size` 报 2064x3096（竖向物理尺寸），但 `input tap` 使用横屏 3096x2064 坐标。

---

## 八、交付物

| 文件 | 状态 |
|---|---|
| `dist/bpp-modpack.zip` | 已重打包（含白块加固） |
| `dist/SHA256SUMS.txt` | 已重跑，6/6 自校验一致 |
| `dist/README.md` | 已按 10-24 事实重写（签名更正） |
| `android-port/HANDOVER-CURRENT-20261023.md` | 已更新（§1 编译、§6.1/6.2/6.3、§10） |

---

## 九、下一轮待办

1. `multi_tier_passive_tooltips`（与 `package_merchant_summary` 同机制，改一处可同时验证）
2. `voice_callbacks`：改走 eventPath 路径 + 真机探针
3. `upgrade_value_tuples`：换一个返回引用类型的钩子
4. `Plugin.cs:422` 清单同步删减
5. 血条**数值**正确性（目前只验证了“渲染了”）
6. 白块加固 7 项审查意见落地后的二次回归
