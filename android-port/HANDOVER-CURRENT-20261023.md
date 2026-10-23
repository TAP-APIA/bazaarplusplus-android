# BazaarPlusPlus 安卓移植 — 当前状态交接

> 生成时间：2026-10-23 15:55 (+08:00)　|　最后更新：2026-10-23 16:12 (+08:00)：血条就绪防护、新 modpack、校验和重建
> 用途：交给接手 Agent 的最小上下文。详细信息见同目录 `HANDOVER-ANDROID-PORT.md`（505 行，10-23 14:49 最后更新）。

## 一句话

The Bazaar（Unity 6000.3.11f1 / IL2CPP arm64，包名 `com.tempogames.thebazaar`）的外置 BepInEx 插件移植：四个触屏面板可用，账号实测已推进到第 5 天，录制回放修到 bootstrap6，**血条适配尚未真机回归**。交付物已打包，测试平板当前离线。

## 当前交付物

| 文件 | 大小 | 实际 SHA256 | 状态 |
|---|---|---|---|
| `dist/BepInExLauncher-android-updated.apk` | 99,348,466 B | `80379ca3bf211233c60005b51acedbb849f3a0805e460f8d9c020b2b1f25be83` | bootstrap4 交付版，平板已覆盖安装 |
| `dist/bpp-modpack.zip` | 3,186,880 B | `63ff08e0396617906acb3f795b192a6420d0a6f2b4819d1f46222d6f3892fe3f` | 10-23 16:06，含 bootstrap6 + 血条就绪防护；**尚未上真机** |
| `dist/SHA256SUMS.txt` | — | — | 10-23 16:09 已重新生成并自校验通过 |

当前构建：0 error / 86 warning（`build-healthbar-guard.log`，16:04，含血条防护）；178 项纯行为测试通过（`test-healthbar-guard.log`，16:07）。

## 已经做完的

1. **构建与部署链**：.NET 8 SDK（`C:\Users\administrator\.dotnet\dotnet.exe`）+ `-p:BppAndroid=true`；插件必须双写 `BepInEx/plugins` 与 `modpacks/bpp/plugins`，否则启动时被 modpack 覆盖。
2. **四个触屏入口**：配装 / 图鉴 / 历史 / BPP，`sortingOrder=32000`，三指点击兜底。
3. **14 类 IL2CPP 崩溃根因**已定位并修复（Newtonsoft 托管版冲突、Runtime 三个补丁、GetWorldCorners 静默失效、foreach Transform、Nullable、InstanceId 字典、TryCast、字段是 Property、SynchronizationContext、GameObject 构造、UITK Allocate、排序层级、Harmony 返回类型、Tooltip 池破坏）。逐条见 `HANDOVER-ANDROID-PORT.md` §三，**不要回退**。
4. **账号实测**：run `98086029` 从第 3 天推到第 5 天，完成商店购买、存箱移回、白银升级、PvE、PvP（对手 Scythes），无现实货币消费。
5. **录制**：确认实际 Data Root 在 `/data/user/0/com.bepinex.android/app_bepinex_runtime/com.tempogames.thebazaar/BazaarPlusPlusV6`，已产出 `CombatReplays/00f38840...payload.mpack.gz`（49,050 B）。
6. **回放链**：bootstrap2 播放到结尾但对手棋盘空、Continue 崩溃 → bootstrap3 修好退出（InstanceId 字典 ABI）→ bootstrap4 恢复 Hand 与头像 → bootstrap5 恢复对手物品 → bootstrap6 血条适配已编译进包。
7. **SQLite**：`libe_sqlite3.so` 放进 APK `lib/arm64-v8a/`，历史面板已能显示本地记录。

## 还没做完 / 风险

1. **血条 NRE 已真机确认修复**：bootstrap5 的崩溃是 `HealthBarController.ForceEmptyHealthVisual` 读了未被 `Init` 写入的 `HealthBarControl`(0x178)。现改为 `Init` 后校验该字段非空才算就绪，未就绪不登记 controller、不调用原生空/刷新血条路径，改记降级事件。真机复核：`account-test-healthbar-*-final.log` 与 logcat 中 `ForceEmptyHealthVisual` / `NullReferenceException` / `Il2CppException` / `Fatal signal` 均为 **0**，`player_attributes_unavailable` 未再出现，进程存活。
   - 仍未证明的是**血条数值正确**：截图里绿色血条和数字确实画出来了，但 OCR 未读出可靠数值（`ocr-hp.tsv` 仅表头），只能说“渲染了”，不能说“数值对”。
2. **回放终止事件缺失（当前最需要修的缺口）**：真机跑完只有 `combat_replay.playback.started`，`playback.succeeded` / `degraded` / `failed` 全为 0，日志停在 `started`。
   - 已定位机制：`ReplayPlaybackLogOperation.Complete()` 是唯一出终态事件的地方，由 **ReplayState 退出**（`SavedReplayLifecycle.OnReplayStateExited`）驱动，`end_reason_code` 为 `saved_replay_exit` 或 `state_exit`。
   - `Events.ReplayEnded` 的托管处理器 `OnNativeReplayEnded` 首行是 `if (!currentNativeRecording && !managedRecordedReplay) return;`——本次是 `source=local_saved`、`record_video=false`，**不在录制态**，所以该 handler 直接返回，不会补任何终态事件。
   - 对照：bootstrap5 之所以有 `end_reason_code=saved_replay_exit duration_ms=134381`，是因为当时确实走到 Continue 让 ReplayState 退出。所以缺口本质是**回放没有自然结束到 recap/Continue 那一步**，而不是日志机制坏了。
   - 下一轮应先确认回放到尾是否自动弹 recap（`BoardManager.ShowRecapView`）、`BoardRecapReplayButtonsController.Show` 的 Harmony Postfix 是否触发（recap 动作用它绑定），再决定是补自动收尾还是补埋点。
3. **Harmony 降级 10 个**：3 个确认不可修（`LoadArt`、`GetPassiveTooltipBlock`、`SetupLockVariantPanel` Transpiler），其余 7 个需看 `LogOutput.log` 的 `Failed to patch` 行。
   - 注意：`sub_logoutput.log` 里的 4 条 `Cannot assign method return type Il2CppSystem.Threading.Tasks.Task` 是 10-22 12:12 的旧证据，早于 `#if BPP_ANDROID` 改造；bootstrap4/5 及本轮日志均无 `patches.degraded`，不要按旧日志改回去。
   - `[Warning:Il2CppInterop] Method ... has unsupported parameter System.Action` 这类是托管方法暴露给原生的注册期噪声，不代表 `BindNativeRecapAction` 绑定失败。
4. **签名**：平板当前装的是 Android Debug 证书，`tools/launcher/bpp.keystore` 是另一套。覆盖升级必须传 `-KeyStore "$env:USERPROFILE\.android\debug.keystore" -KeyAlias androiddebugkey`，否则 `INSTALL_FAILED_UPDATE_INCOMPATIBLE`。
5. **交付物**：`SHA256SUMS.txt` 已重建并 6 项自校验通过；本轮部署的主 DLL `ae39d333eba4dd347f04e7b541b0d087f600828a371d6e2699535ec62952f639` 在 `BepInEx/plugins` 与 `modpacks/bpp/plugins` 双写一致；APK 本轮未变。
6. **重复进入部分验证**：新进程内可再次进入回放且无崩溃关键词；**同一会话内第二次进入未触发** `playback.started`，仍需排查。
7. **设备连接**：平板无线调试端口会变，本轮为 `192.168.0.14:37699`；`adb mdns services` 常为空，不要据此判断设备不存在。

## 环境

- 设备：vivo PA2573 平板，Android 16，序列号 `400F45F1AX00000`，锁屏 PIN `230315`。
- adb：`D:\thebarzaar\tools\platform-tools\platform-tools\adb.exe`。
- 游戏：`1.0.12825-production-android-d9601ded`，启动器 `com.bepinex.android`，游戏进程 `com.bepinex.android:game`。
- 工作区：`D:\thebarzaar\`（`android-managed` 176 个代理 DLL、`dumped3/dump.cs`、`BazaarPlusPlus` 源码、`dist` 交付）。

构建：

```powershell
$env:DOTNET_ROLL_FORWARD="Major"
& C:\Users\administrator\.dotnet\dotnet.exe build `
  "D:\thebarzaar\BazaarPlusPlus\bazaarplusplus-mod\src\BazaarPlusPlus\BazaarPlusPlus.csproj" `
  -c Debug -p:BppAndroid=true -p:ManagedPath=D:\thebarzaar\android-managed `
  -p:NuGetAudit=false -p:TreatWarningsAsErrors=false --nologo
```

部署：`dist\install-to-device.ps1`（双写）；打包：`dist\package-modpack.ps1 -Zip`；启动器：`dist\package-launcher.ps1`。

## 写代码前必读

- 强转一律 `AsIl2Cpp<T>()` / `AsRectTransform()` / `AsImage()`，字段用 `BppFieldAccessor`，世界坐标用 `GetWorldCornersManaged`。
- `foreach (Transform child in parent)` 会抛 `InvalidCastException`，改 `GetChild(i)`。
- `Il2CppSystem.Nullable<T>` 的 `default` 是 null，要 `new`。
- 异步必须 `.AsManagedTask()`，禁止 `while (!t.IsCompleted) await Task.Yield()`。
- `Dictionary<InstanceId, X>` 的托管泛型调用会 SIGSEGV，改走 `TheBazaar.Data.GetCard` 等原生入口。
- 启动流程必须 `uiautomator dump` 按 `text=` 定位，不要硬编码坐标。

## 建议下一步

1. `adb connect` 把平板接回来，冷启动确认 `plugin.initialization.succeeded`，游戏进程活过 60 秒。
2. 跑一次完整回放（历史 → 第 4 天 Scythes → 播放 → Continue → 回主界面），验证 bootstrap6 血条与退出都不崩。
3. ~~重新生成 `dist/SHA256SUMS.txt`~~ **已完成 16:09**，6 个条目自校验 0 不符；后续只要再动 `dist` 里的文件就必须重跑一次。
4. 7 个 Harmony 降级按 `LogOutput.log` 逐条处理（当前最新真机日志未列出具体失败行，需接回设备后抓 `LogOutput.log`）。
5. 需要覆盖升级时用 debug keystore 签名，别用 `bpp.keystore`。
6. 接回设备后先只推 modpack（`install-to-device.ps1`），不要重装 APK，以免动到内部数据目录里的回放文件。

## 文档地图

| 文件 | 作用 |
|---|---|
| `HANDOVER.md` | 2026-10-21 初始任务书 + metadata v39 逆向结论（4 个阶段目标已达成） |
| `HANDOVER-ANDROID-PORT.md` | **主文档**：环境、14 条崩溃修复、踩坑表、代码地图 |
| `dist/README.md` | 交付与安装说明（10-23 16:09 版） |
| `HANDOVER-CURRENT-20261023.md` | 本文件，最小交接摘要 |
