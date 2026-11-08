# 大巴扎助手 Android / BazaarHelper

《The Bazaar》（大巴扎）的手机端助手：在 **不修改游戏 APK** 的前提下，启动游戏、只读采集真实对局战绩、同步到微信小程序，并在战斗时显示悬浮计时。

下载与版本记录：**https://bazzarhelper.com/**

> 本仓库是 [TAP-APIA/bazaarplusplus-android](https://github.com/TAP-APIA/bazaarplusplus-android) 的 fork，保留了上游 Android 移植的源码作为参考；实际发布的产品是 `bazaar-helper-android/`（包名 `com.bazzarhelper.android`，独立 UID），它的详细说明见 [bazaar-helper-android/README.md](bazaar-helper-android/README.md)。

## 功能

| 功能 | 说明 |
| --- | --- |
| **启动游戏** | 从助手进入已安装的游戏，复用游戏自身的 Activity，不改游戏包、不重签、不覆盖 |
| **只读采集战绩** | 真实 PvP 的双方棋盘、技能、属性/附魔/格位、结算与战斗指标；怪物 / PvE 对局不采集 |
| **同步到小程序** | 结算后按当时的账号归属上传既有 v3 API；失败与不完整回执自动重试，成功后内容指纹落盘 |
| **对局从开局可见** | 一开局就把 run 上传，进行中就能在小程序里查看、分享；是否公开由「分享新战绩」开关控制（默认开） |
| **战斗悬浮计时** | 战斗中显示可拖动的用时计时，只影响显示，不改变游戏速度与结算 |
| **账号绑定** | 微信扫码登录（保存小程序码到相册再识别，单机也能完成），凭据用 Android Keystore 加密 |
| **首次启动准备** | 第一次启动或游戏更新后生成游戏代理，居中卡片显示当前阶段、预计耗时与已用时间，生成阶段停在 92% 等真正完成再走到 100% |
| **诊断日志** | 首页「导出日志」，打包运行时信息与滚动日志，便于反馈问题 |

## 下载

| 渠道 | 地址 |
| --- | --- |
| 官网（推荐，含夸克网盘备用） | **https://bazzarhelper.com/** |
| 最新安装包（GitHub） | https://github.com/dabazzar/BazaarHelperAndroid/releases/latest |
| 全部版本记录 | https://github.com/dabazzar/BazaarHelperAndroid/releases |

- 当前版本 **0.1.11**，要求 **Android 10+（minSdk 29）、arm64-v8a**，安装时需要允许「安装未知来源应用」
- 安装后需要与游戏同机运行，并用微信扫码绑定账号；未绑定时只能停在登录页

### 小程序

查图鉴、看战绩、回看对手棋盘都在微信小程序里；装好助手后用它完成扫码绑定。

<img src="assets/bazaarhelper-wechat-mini-program.png" width="560" alt="微信扫一扫，打开大巴扎助手小程序">

## 实现原理

游戏是 Unity 6000.3 / IL2CPP / arm64-v8a，助手不去改它，而是把「已经装好的游戏」托管到自己进程里跑，再用只读 Hook 观察它的网络消息与战斗结算。

```
┌─────────────────────────────── 助手 APK（com.bazzarhelper.android，独立 UID）──────────────────────────────┐
│ Java 应用层  ui/ sync/ data/    登录、同步、SQLite(WAL) 战绩库与 outbox、Keystore 加密凭据、界面          │
│      ▲ JNI（无网络调用，只在回调里投影成白名单 JSON）                                                     │
│ runtime/GameRuntime              用 PathClassLoader + Instrumentation 重定向把游戏的 UnityPlayerActivity    │
│                                  跑在助手 UID 下；游戏仍从它自己的 APK 读取资源与 native 库                 │
│      ▲ BepInEx / .NET（固定版本 NextBep/BepInEx.Android.Launcher@44e1e59）                                 │
│ hook/BazaarHelper.Hook.dll       Harmony patch NetMessageProcessor.Receive / CombatSimHandler.Simulate，     │
│                                  只读观察 PvP 开局、结算与战斗帧，不写游戏状态                              │
└──────────────────────────────────────────────────────────────────────────────────────────────────────────┘
                                   │ 结算后 HTTPS
                                   ▼
                        既有 v3 API → 微信小程序（战绩、分享、社区）
```

关键点：

- **不改游戏包**：助手用独立 UID 与独立私有目录；游戏的 `assets/bin/Data`、interop 代理都在助手私有目录重新生成，原游戏 APK 只读。
- **首次生成代理**：从游戏包提取元数据后用 Cpp2IL / Il2CppInterop 生成代理程序集（首次约 1–2 分钟），只在游戏版本变化时重做；Unity 运行库随 APK 内置，命中时无需联网。
- **只读采集**：只监听消息分发与战斗模拟回调，记录双方棋盘、技能、结算与指标；不保存原始网络消息、录像或对手身份，不修改游戏速度、结算与随机数。
- **采集边界**：只有对手是真实玩家（英雄可解析）时才算一局 PvP，怪物 / PvE 战斗直接跳过；同一 run 同一自然日只记录一场。
- **上传**：结算时固定账号归属，匿名记录不在后续绑定时认领；切号不能转移历史；跨进程上传锁 + 内容 SHA-256 + 严格回执数量，避免漏传、重复确认与迟到 401 清掉新会话。
- **悬浮计时**：宿主在游戏 DecorView 上加一个不拦截触摸的计时条，按逻辑帧累计；开关与位置存在共享键值表里，两个进程共用。

## 目录

| 路径 | 内容 |
| --- | --- |
| `bazaar-helper-android/` | 发布的产品：Java 应用、.NET Hook、SDK 构建脚本、CI workflow、设备测试 |
| `android-port/`、`bazaarplusplus-*/`、`release/` 等 | 上游 BazaarPlusPlus Android 移植的源码，作为参考保留；不属于发布包 |

## 构建与验证

```sh
cd bazaar-helper-android
python3 tools/build.py --interop .local/game-interop   # SDK 直接构建（生成 interop 是编译输入，不入库）
./gradlew :app:assembleDebug                           # 或走标准 Gradle
python3 tools/check-apk.py build/BazaarHelper-*.apk    # 产物范围检查
```

CI（`.github/workflows/bazaar-helper-android.yml`）在推送 `master` 或打 `bazaar-helper-v*` tag 时构建并签名；签名与游戏 interop 都走仓库 secrets，不进仓库也不进 APK。真机测试脚本与更多细节见 [bazaar-helper-android/README.md](bazaar-helper-android/README.md)。

## 数据边界

- 只上传规范化后的双方棋盘、技能、属性、结算与战斗指标；不保存原始游戏消息、录像、对手身份或账号密码。
- 凭据与战绩库放在 `no_backup` 目录，不导出、不自动备份；凭据用 Android Keystore 的 AES-GCM 加密。
- 新战绩默认随「分享新战绩」开关公开到社区（默认开启），关闭只影响之后的对局；公开状态是单向的，已公开的局不会因为关开关而取消。

## 许可证

[MIT License](LICENSE)

## 致谢

- [TAP-APIA/bazaarplusplus-android](https://github.com/TAP-APIA/bazaarplusplus-android)：本仓库的 fork 来源，Android 移植的 native 加载与游戏托管方案。
- [BazaarPlusPlus/BazaarPlusPlus](https://github.com/BazaarPlusPlus/BazaarPlusPlus)：原始的桌面端模组项目。
- 以及 BepInEx / Il2CppInterop / NextBep 等开源项目，助手只使用它们的公共运行时基础设施。
