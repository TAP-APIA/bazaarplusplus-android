# 大巴扎助手 / BazaarHelper Android

独立 Android 应用，包名 `com.bazzarhelper.android`。源码、构建和私有数据与原 BPP 启动器隔离。

范围：微信扫码绑定；真实 PvP 的双方物品/技能、结算与指标采集；本地战绩；按结算时账号归属上传既有 v3 API。
不包含图鉴、配装、推荐、回放、截图、游戏内面板、第三方账号、模组市场或自动游戏操作。

## 架构

- `app/`：独立 Java Android 界面、SQLite 战绩/队列、Android Keystore 凭据和同步任务。
- `hook/`：独立 .NET IL2CPP 只读 Hook，仅观察游戏网络分发与战斗结算，白名单投影后交给应用。
- `tools/`：固定依赖校验、构建与 APK 内容检查。
- `third-party/`：底层加载依赖的来源、版本、哈希与许可证；不包含任何 BazaarPlusPlus 业务程序集或界面。

底层需第三方 Android 游戏加载器、CoreCLR、BepInEx 和 Il2CppInterop；这些是运行 Hook 的基础依赖，不是本项目的产品界面。
游戏本体仍单独安装，不修改原游戏 APK。第一次或游戏更新后会在独立目录生成匹配的代理程序集。

## 数据边界

凭据与 SQLite 数据库放在 `no_backup` 中，不导出、不自动备份。新应用独立扫码绑定，不读取旧应用的私有凭据。
只上报玩家之间的对战：Hook 记录开局前先确认对手是玩家（消息里的 `PvpOpponent`，回退到游戏的 `Data.SimPvpOpponent`，与桌面端同一顺序），两者都拿不到或英雄是占位值 `EHero.Common` 时视为怪物 / PvE 并跳过；同一 run 的同一自然日只记录一场，结算后立刻清空开局快照，避免客户端重发 `CombatSim` 造成一天多条。

真实 PvP 在结算时固定归属；未绑定时的本地记录不会在后续绑定时认领。切号不能转移历史战绩。

一局在上传端从开局就存在：Hook 在 `NetMessageRunInitialized` 时就把 runId / 模式 / 玩家英雄交给应用，应用存进 `run_facts`（含归属账号），同步时把「本账号名下还没有战绩的局」也一起上传，所以第一场战斗之前小程序就能看到并分享这一局。「分享新战绩」开关在账号卡片里，默认打开（与桌面端默认一致），值存在数据库 `shareRuns` 并随每次上传写进 `shareEnabled`；`completedAt` 与桌面端一致：这一局只要已经有一场战斗，就用最后一场战斗的时间填 `completedAt`（桌面端 `main.tsx` 也是这么传的），所以进行中的局也满足小程序「发布到社区」与服务端发布的前置条件，并且会像桌面端一样在进行中就出现在需要 completedAt 的列表里。关闭开关只影响之后新开的局：服务端的 public 标记是单向的，已公开的局不会因此取消。

只上传规范化的双方棋盘、技能、属性、结算和指标；不保存原始游戏消息、录像或对手身份。
网络失败与不完整回执保持待重试；成功内容指纹落盘后才确认上传，战绩默认私有。

双方物品的附魔来源不同：对手的来自战斗消息里的卡牌更新（本地没有对手卡可读），我方的在消息里恒为空（`SimUpdateCard.Enchantment` 不随自己已有的卡下发），所以从本地 `ItemCard.Enchantment` 读取，与桌面端「有本地卡就读本地卡」的顺序一致；技能没有该成员，读取按「无附魔」处理。

## 构建

要求 JDK 17、.NET SDK 10、Android SDK 36 / Build Tools 36.0.0、NDK 27.0.12077973、CMake 3.22.1；当前只生成 ARM64，最低 Android 10。

首次构建准备当前游戏生成的 `interop/*.dll`，放到 `.local/game-interop/`，或传 `--interop /绝对路径/interop`。这是游戏 API 的编译输入，不包含 BPP，不打包。运行时始终针对手机当前游戏在本应用私有目录生成自己的代理。

```sh
# SDK 直接构建：Javac + D8 + AAPT2 + CMake + dotnet，包含签名与内容检查
python3 tools/build.py

# 标准 Android Studio / Gradle 项目（wrapper 固定 8.11.1）
./gradlew :app:assembleDebug

# 明确指定编译代理
python3 tools/build.py --interop /path/to/current/interop
./gradlew :app:assembleDebug -PgameInterop=/path/to/current/interop
```

默认 SDK 在 `$HOME/Library/Android/sdk`，JDK 在本机 Microsoft JDK 17 路径，可用 `ANDROID_HOME` / `JAVA_HOME` 覆盖。首次获取第三方源码可通过 `HTTPS_PROXY` 指定网络代理，不改全局 Git / 系统代理。

## 登录

未绑定微信时只能看到登录页，首页（启动游戏 / 计时器 / 战绩 / 同步）在绑定成功前不构建，因为每条战绩在结算时就要固定归属。账号管理里退出会立即回到登录页。

目前只有一个入口：**微信扫码登录**。向服务器申请一次性绑定会话，期间显示不可取消的加载面板（服务器要现场调用微信接口生成小程序码），成功后弹出小程序码。主按钮「保存到相册」把码写进相册（不自动跳转微信，弹窗保持打开，可反复保存），再到微信「扫一扫 → 右上角相册」选择该图确认——单机也能完成；也可以直接用另一台设备扫码。

早先还有一条「微信登录」直达入口（读 API 的 `deeplink`，服务器侧用 `wxa/generate_urllink` 生成 URL Link）。小程序主体没有 URL Scheme / URL Link 权限（微信返回 85407），该字段一直为空，只能失败后再引导去扫码，按用户要求移除，客户端不再读取 `deeplink`。

会话未确认前登录页显示「等待微信确认」并每 2.5 秒轮询，确认后自动进入首页；再次点击会先丢弃未认领的旧会话（`SyncEngine.abandonLogin`）再申请新码，等待中也可以「取消这次登录」回到初始态。已绑定账号调用该方法会被拒绝。

## 图标

图标用设计侧提供的 flat 自适应图标资源（已整套拷进 `app/src/main/res/`）：全部画面都在**满幅、不透明**的背景层里（432×432，没有外金框、没有预圆角，由系统遮罩裁切），前景层是空的透明图，另带 monochrome PNG 与五档传统 PNG。

- `drawable-nodpi/`：`ic_launcher_background.png`（满幅画面）、`ic_launcher_foreground.png`（透明）、`ic_launcher_monochrome.png`（Android 13 主题图标剪影）。
- `mipmap-{mdpi..xxxhdpi}/ic_launcher(_round).png`：Android 7 及更早用的传统图标。
- 背景不透明且满幅，所以启动器遮罩里不会出现"透明背景→深色/黑色底板"的黑边或接缝（早期版本就是因为背景透明被 MIUI 铺成黑底）。
- 应用内首页标题左侧的徽章解码 `@drawable/ic_launcher_background`（前景层是空的）。
- 图标资源由设计侧提供，仓库里直接保存 PNG/XML，没有生成脚本。

## 界面

配色沿用图鉴主题 `miniprogram/theme.wxss` 的变量（`--ui-bg 101c26`、`--ui-panel 1b2b36`、`--ui-raised 263c48`、`--ui-ink ecede4`、`--ui-muted a0b2bb`、`--ui-accent e8b95d`、`--ui-line 3c5360`，胜负色 `73cbb0 / f2a194`），组件节奏参考多邻国：实心底部厚边的胶囊按钮、按下时下沉、卡片粗描边、分区小标题（大写字母 + 字距）、圆角药丸标签。

- `ui/Ui.java`：调色板、间距、字号、卡片 / 药丸 / 分隔线、用 Path 现场绘制的图标（不打包图片资源）。
- `ui/ChunkyButton.java`：主 / 次 / 危险三种按钮。高度按字体缩放计算，避免大字号裁切；按下动画受系统“动画时长”开关控制。
- `ui/Sheet.java`：应用内对话框。标题与按钮固定，内容区滚动，长列表不会把「关闭」挤出屏幕。

首页按功能分区，一屏内可完成全部操作：账号（绑定状态 + 扫码 / 账号管理）、开始记录（启动游戏 + 采集状态指示灯）、计时器（显示开关 + 恢复默认位置）、战绩（本账号场次 + 查看 / 立即同步），最下面「导出日志」。标题左侧直接用应用图标（自适应图标前景裁到徽章区域），不再画独立的字标。战绩弹层只做每日汇总：每天一行「N 胜 M 负」，不展开单场棋盘明细。

交互约定：主按钮展示进行中状态（`准备中…` / `同步中…`）并禁用重复点击；准备游戏期间用不可取消的进度面板展示真实阶段；失败与空状态用同一套面板说明原因，不用系统弹窗。

## 启动提示

启动前先做只读检查 `RuntimeInstaller.preview()`，用和 `prepare()` 相同的 stamp 判断这次要不要干活：

- `runtime.version` 不匹配 → 要解包 BepInEx / .NET 运行时；
- `game.version` 与游戏 APK 指纹（路径 + 大小 + mtime）不一致 → 要提取游戏元数据并重新生成代理（Unity 运行库若已内置就不再下载）；
- `BepInEx/unity-libs/<Unity 版本>.zip` 缺失或不是有效 zip → 安装包里有同版本内置副本就直接解出（无需联网），否则从 `unity.bepinex.dev` 下载几 MB。

需要干活时才弹「先准备运行环境」面板：列出这次会做的事（下载依赖 / 解包运行时 / 重新生成代理），说明约 1–2 分钟、建议 Wi-Fi、保持前台，给「开始准备 / 取消」；环境已就绪时点「启动游戏」直接进游戏，不再打扰。若这次确实要联网下载（安装包里没有匹配的 Unity 基础库）而当前没有网络，直接提示先连网，不进入准备流程（避免下到一半失败）。准备过程中原来的进度面板照常显示真实阶段文案。

游戏本体首次运行还会自己下载资源包，那部分不经过助手。

## 诊断日志

`Diag` 每个进程一条滚动日志（`filesDir/app.log`、`:game` 进程写 `game.log`，超过 256KB 轮转到 `.1`），记录应用生命周期、登录请求与结果、启动准备、同步结果、计时器开关、界面提示，并挂上未捕获异常处理器（崩溃堆栈进日志后仍交给系统处理）。手机上的 logcat 普通应用读不到，所以这些事件同时写 `Log.i/Log.e` 便于 adb 现场排查。

首页最下面的「导出日志」打成 zip：`bazaarhelper-diag-<时间>.zip`，内含 `report.txt` 与 `logs/app.log`、`logs/game.log`（有轮转则还有 `.1`），`application/zip`、最高压缩级别（10 万行级别的文本通常压到十分之一）。文件写入 `Download/BazaarHelper/`（MediaStore，无需 FileProvider），随后弹出系统分享面板。报告包含版本、机型/系统/分辨率/ABI、游戏进程是否在跑、账号（别名、有无凭据、代次前 8 位、owner 前 8 位、本地战绩条数）、同步与采集状态、计时器开关与位置。token 与完整账号标识不写入报告。

## CI 打包

仓库根的 `.github/workflows/bazaar-helper-android.yml` 在推送到 `main`、打 `v*` tag 或手动触发时，用 GitHub 托管 runner 出签名 APK；tag 构建会自动把 APK 与 `build-manifest.json` 附到同名 release。本地 `python3 tools/build.py` 行为不变：没有 `ANDROID_KEYSTORE` 时继续用 `.keystore/development.p12`。

runner 需要 `platforms;android-36`、`build-tools;36.0.0`、`ndk;27.0.12077973`、`cmake;3.22.1`（workflow 用 sdkmanager 安装），以及 .NET 10 与 JDK 17。

需要在 Settings → Secrets and variables → Actions 配置：

| Secret | 用途 |
| --- | --- |
| `BAZAAR_HELPER_KEYSTORE_BASE64` | 发布签名库的 base64：`base64 -i ~/.android/keys/novaparty-release.jks` |
| `BAZAAR_HELPER_KEYSTORE_PASSWORD` | keystore 口令 |
| `BAZAAR_HELPER_KEY_PASSWORD` | key 口令（一般与 keystore 相同） |
| `BAZAAR_HELPER_KEY_ALIAS` | 可选；库里有多个 key 时才需要，`keytool -list -v -keystore …` 第一行就是 |
| `BAZAAR_HELPER_INTEROP_URL` | 游戏互操作 DLL 压缩包地址。私有 release 用 **API asset 地址**（`https://api.github.com/repos/<org>/<repo>/releases/assets/<id>`）——浏览器 `/releases/download/...` 形式不接受 token、会 404 |
| `BAZAAR_HELPER_INTEROP_TOKEN` | 可选；访问上面的地址需要 Bearer token 时填 |

游戏互操作 DLL 是游戏逆向产物，既不能进仓库也不会打进 APK，CI 从私有压缩包还原：

```sh
cd .local && zip -qr /tmp/game-interop.zip game-interop
```

压缩包里可以直接是 DLL，也可以带一层 `game-interop/` 目录，workflow 两种都认；缺少 `BazaarGameShared.dll` 会直接失败。私有 asset 需要 `BAZAAR_HELPER_INTEROP_TOKEN`（fine-grained PAT，Contents 只读）。

## 战斗计时

只读显示，不改游戏速度、不提供暂停。`CombatSimHandler.Simulate` 收到战斗时按帧总数开始，`FinalBlowSlowDownController.Process` 每处理一个逻辑帧前进 50ms（首帧为 0），`FinishCombat` 冻结最终用时，`CancelCombat` / `OnCloseRun` / 新开局隐藏。Hook 安装失败只记警告，不影响战绩采集。

显示由宿主 Java 在游戏 Activity 的 DecorView 上添加计时条：默认底部居中，内边距 14dp × 6dp，距底部 24dp 并叠加系统栏 / 刘海安全区。按住计时条可拖到任意位置，松手后按“左上角占可移动范围的比例”写入数据库，旋转或换分辨率后仍落在相近位置；拖拽超出边界会贴边。只有计时条本身接收触摸，其余区域照常交给游戏。位置以 0–1 比例存储，`恢复默认位置` 清空即回到底部居中。

助手界面「悬浮计时器」卡片提供显示开关（写同一个键值表）；游戏进程在 Activity 每次 resume 时重新读取开关与位置，因此改完回到游戏即生效。计时条隐藏时不影响战绩采集。

```sh
dotnet run --project tests/clock/ClockTests.csproj          # 11 项逻辑帧换算
ADB_PORT=5038 ANDROID_SERIAL=DEVICE TEST_RUNNER=TimerOverlayTests python3 tools/test-device.py   # 28 项布局 / 拖拽 / 开关 / 格式
```

SDK 构建产物 `build/BazaarHelper-0.1.11.apk`，逐依赖 / 游戏代理哈希在 `build/build-manifest.json`；Gradle 产物在 `app/build/outputs/apk/debug/`。开发签名私钥位于 `.keystore/`，被 Git 忽略，保留它才能覆盖安装同一签名的后续 SDK 构建。正式分发签名需单独管理。

```sh
adb -s DEVICE install --no-incremental -r build/BazaarHelper-0.1.11.apk
adb -s DEVICE shell am start -n com.bazzarhelper.android/.ui.MainActivity
```

旧应用不卸载。新包拥有独立 UID、游戏登录缓存、助手微信绑定和战绩库，需要重新登录。原游戏 APK 仍单独安装，助手不重签或覆盖它。游戏首次启动 / 更新生成代理较慢，界面用居中卡片显示阶段、该阶段预计耗时、从 00:00 累加的已用时间与百分比（生成代理停在 92% 直到真正完成，再到 100% 淡出；超过 3 分钟会提示「已超出预计时间，仍在生成」），期间应保持前台。

Unity 基础库（`unity.bepinex.dev` 的 `<Unity 版本>.zip`，约 3 MB、78 个 `UnityEngine.*.dll`）打进 APK 的 `assets/unity-libs/`，由 `third-party/unity-libs.json` 按「游戏 Unity 版本 → 上游 zip 版本」显式映射并固定 SHA-256，构建时 `tools/prepare-dependencies.py` 取回 `.local/assets/unity-libs/<游戏版本>.zip`（pin 里删掉的条目会在构建时自动清理）。`RuntimeInstaller.bundledUnityLibs` 只做**文件名精确匹配**：游戏 Unity 版本与条目完全一致才用内置副本，任何不一致都回退网络下载，由下载路径按 BepInEx 的「同版本线就近取旧」规则处理，运行时不做任何版本猜测。当前条目是游戏 `6000.3.27` → 上游 `6000.3.26`（镜像最高只到 26，下载路径本来就回退到这版）。

## 验证

```sh
# 在指定测试设备的 ART / SQLite / Android Keystore 中执行，HTTP 为模拟传输
ADB_PORT=5037 ANDROID_SERIAL=emulator-5580 python3 tools/test-device.py

# 其余 runner：TimerOverlayTests（计时条，需解锁屏幕——锁屏时无法旋转到横屏）、
# ExportTests（诊断压缩包）、LaunchNoticeTests（启动提示，会临时改 stamp 再还原）、
# PrepareTests（真实准备，需网络）
ADB_PORT=5038 ANDROID_SERIAL=DEVICE TEST_RUNNER=LaunchNoticeTests python3 tools/test-device.py
python3 tools/check-apk.py build/BazaarHelper-0.1.11.apk
```

同步测试使用独立测试数据库与密钥别名，不写真实战绩或请求真实账号接口，覆盖 26 项断言：加密、v3 绑定、固定账号归属、匿名隔离、重复采集、不完整回执、内容变化、切号期间的迟到响应 / 401、多次离线退出、刷新响应丢失恢复、SQLite 重开与 JSON 指纹顺序。上传锁按数据库文件命名，测试库不会被应用后台同步的锁挡住。

`RuntimeSmoke` 是单独显式调用的启动探针，不参与同步测试。只启动游戏、等待 Hook 状态，不登录、不开局；超时不视为成功。Hook 启动另做 native nullable 空值及枚举 0 的内存 DTO 验证，不生成消息、战绩或上传。

真实验收：微信扫码绑定 → 从助手启动游戏 → 用户完成一场真实 PvP → 本地记录核对双方物品 / 技能 / 结果 → 小程序核对上传。不能把模拟 HTTP、构建通过或 Hook 就绪等同于实战验收。

## 实现边界

- Java SQLite WAL 为唯一战绩持久化写入者。Hook 只投影协议白名单，不落原始消息 / 回放。
- `NetMessageProcessor` 分发兼容聚合消息；观察开局时保存不可变双方快照，`CombatSimHandler.Simulate` 捕获结算及按卡统计 / 效果来源指标。不改变游戏行为。
- `OptionalValue` 区分 native 字段和 getter，直接读取 nullable 的空指针 / boxed enum；不构造不存在的 nullable 代理。`EffectSource` 对 boxed InstanceId 解箱后读取 Value。
- JNI 只负责转交 JSON / 账号代次；Java 工作线程负责 HTTP。两个进程用 SQLite 和上传文件锁（`<数据库文件>.upload.lock`）协调，不持有数据库事务做网络请求。
- 回执只有数量完全匹配并且账号代次 / token 仍匹配时确认；内容指纹发生变化后会重新上传。每周刷新 token，先保存候选 token，可恢复服务端已轮换但响应丢失的情况。
- 退出登录先立即清本机会话，撤销请求的 token 加密排队重试。本地历史按 accountId 隔离，同一账号重登可见，其他账号不能认领。
