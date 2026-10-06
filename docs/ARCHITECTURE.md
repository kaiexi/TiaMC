# TIA-MC 启动内核架构说明

本文按"启动一次游戏"的时间顺序，说明 `TiaMc.Core` 每个模块做什么、为什么这样做，
以及它与 ColorMC（`ColorMC.Core`）对应实现的差异。

---

## 0. 模块依赖

```
McPaths  ─────────────┐
                      ▼
VersionRepository ─► InstalledVersion ──┐
   (JSON + 合并)                        │
                                        ▼
RuleEvaluator ─► LibraryResolver ─► LaunchPlanner ─► LaunchPlan ─► GameProcess
                                        ▲
JavaDetector ───────────────────────────┘
IntegrityChecker / DownloadService      （启动前的可选步骤）
ManifestClient                          （在线安装新版本）
MinecraftAccount                        （账户信息）
```

内核里没有全局单例、没有静态可变状态（除了 `Utils.AppInfo` 常量），
所有路径都由 `McPaths` 注入，因此**同一进程可以同时服务多个 `.minecraft` 目录**，
也便于单元测试。

---

## 1. `McPaths`：目录布局

```csharp
public string VersionsDir   => Path.Combine(Root, "versions");
public string LibrariesDir  => Path.Combine(Root, "libraries");
public string AssetsDir     => Path.Combine(Root, "assets");
public string NativesDir(string versionId)
    => Path.Combine(VersionsDir, versionId, versionId + "-natives");
```

游戏根目录 `Root` 就是 `.minecraft`（工作目录也是它）。`NativesDir` 采用"每版本一份"的布局，
与 PCL/HMCL 的做法一致，避免多个实例互相覆盖 native DLL。

## 2. `VersionJson` + `ArgumentListConverter`：容错解析

`arguments.game` / `arguments.jvm` 是**混合数组**：

```json
"game": [
  "--username", "${auth_player_name}",
  { "rules": [{ "action": "allow", "features": { "has_custom_resolution": true } }],
    "value": ["--width", "${resolution_width}", "--height", "${resolution_height}"] }
]
```

用一个数组级 `JsonConverter`（`ArgumentListConverter`）把字符串与对象统一成
`ArgumentJson { Rules, Value }`。之所以注册在**列表**上而不是 `ArgumentJson` 上：
若注册在元素上，反序列化对象时要跳过 `value` 字段，而跳过时 `JsonConverter` 仍会参与，
一旦实现有偏差就会无限递归。

`StringOrArrayConverter` 则用于其它"字符串或数组"字段（例如 `LibraryJson` 的扩展场景）。

## 3. `VersionRepository`：继承合并与去重

### 3.1 合并顺序

```
Load("1.20.1-forge-47.4.26")
   │ 读 1.20.1-forge-47.4.26.json   （子）
   │ inheritsFrom = "1.20.1"
   │ 读 1.20.1.json                 （父）
   └► Merge(parent: 1.20.1, child: forge)
```

规则（与官方启动器一致）：

* 标量字段（`mainClass`、`assets`、`assetIndex`、`javaVersion`、`logging`、`type`…）**子版本优先**，子版本为空才取父版本；
* `libraries` 与 `arguments.game/jvm` **父版本在前、子版本在后**（保持 JVM 类加载顺序）；
* `downloads` 按键合并，子版本覆盖同名键；
* `Chain` 记录 "自身 → 根父版本"，用于在两个位置找客户端 Jar、在界面上显示继承链。

### 3.2 客户端 Jar 的定位

```csharp
foreach (var id in chain)                       // 自身优先，然后父版本
    if (File.Exists(VersionJarPath(id))) return id;
```

现代 Forge 安装器会写一份 `<id>.jar`（21 MB 的"client-extra"），
原版 1.20.1 则是自己的 `1.20.1.jar`。二者的差异只体现在文件是否存在。

### 3.3 库去重

同一 `group:artifact` 可能出现两次（原版一次、装载器一次）。规则：
保留**版本更高**的那条，位置沿用**首次出现**的位置。

版本比较用 `CompareVersions`：把版本串按非字母数字切分，逐段比较，
两边都是数字时按数值比，数字段优先于文本段。这样
`9.9.1 > 9.8`、`2.1.10 > 2.1.9`、`1.20.1-47.4.26 > 1.20.1-47.4.2` 都能正确判定
（纯字符串比较会把 `2.1.10` 判成小于 `2.1.9`）。

## 4. `RuleEvaluator`：规则判定

```csharp
public static bool IsAllowed(IEnumerable<RuleJson>? rules, FeatureSet? features = null)
```

* 无 `rules` → 允许；
* 否则**默认拒绝**，按顺序遍历，命中则 `action == "disallow" ? 拒绝 : 允许`；
* `os.name` 取 `windows|osx|linux`；`os.arch` 取 `x64|x86`（`Environment.Is64BitProcess`）；
* `features` 里没写的键一律视为 `false`，所以
  `has_custom_resolution` 为真时 `--width/--height` 才会从 JSON 带出，
  而 `LaunchPlanner` 会先检查这两个参数是否已存在，避免重复追加。

## 5. `LibraryResolver`：maven 坐标 → 本地文件

```
name = "org.lwjgl:lwjgl:3.3.1:natives-windows"
       │                      └ classifier
       └ group:artifact:version

artifact.path = "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar"     -> libraries/<path>
classifiers["natives-windows"].path                          -> natives 解压用
```

* `MavenToPath` 在没有 `downloads.artifact` 的老版本 JSON 里兜底生成路径；
* natives 分类器按 `natives-<os>-<arch>` → `natives-<os>` → `<os>` 的顺序查找；
* 只有 natives 的库（`:natives-windows` 那种）**不进 classpath**，只参与解压；
* `ExtractNatives` 用 `ZipArchive` 解压到 `versions/<id>/<id>-natives`，
  支持 `extract.exclude`，并且对每个条目做目录逃逸检查（`GetFullPath` 前缀比对）。

## 6. `LaunchPlanner`：从 JSON 到命令行

```
- 去重后的库 --规则筛选--> 解析结果 --解压--> natives 目录
                          │
                          └─ artifact 路径 + 客户端 Jar --> classpath（';' 分隔）
参数模板:
  arguments.jvm  ─┐
  arguments.game ─┤ 逐条按规则保留，合并成模板列表
  minecraftArguments (V1 兜底，按空格切分)
基础 JVM 参数: -Xms/-Xmx、GC、-javaagent、用户附加参数
占位符替换:   ${classpath} ${classpath_separator} ${natives_directory} ${library_directory}
              ${auth_player_name} ${auth_uuid} ${auth_access_token} ${user_type}
              ${version_name} ${version_type} ${game_directory} ${assets_root}
              ${assets_index_name} ${launcher_name} ${launcher_version} ...
输出: LaunchPlan { JavaPath, MainClass, JvmArguments[], GameArguments[], Classpath,
                   NativesDirectory, GameDirectory, Environment, RequiredJavaMajor }
```

要点：

1. **`${classpath}` 必须整体替换**：先用 `string.Join(';', ...)` 拼好再替换，且分隔符单独放在
   `${classpath_separator}` 里 —— 新版 Forge 的 `-p` 参数正是用 `...jar${classpath_separator}...jar`
   的形式拼模块路径，直接硬编码 `;` 会在非 Windows 平台出错。
2. **Forge 1.17+ 走 BootstrapLauncher**：主类由 JSON 给出
   （`cpw.mods.bootstraplauncher.BootstrapLauncher`），`-p` 与 `--add-modules ALL-MODULE-PATH`
   同样来自 JSON 的 `arguments.jvm`，启动器**不需要**额外注入 `-DlegacyClassPath`。
   内核只做一件事：识别 `bootstraplauncher` 并在日志里说明（`UsesBootstrapLauncher`），
   以及为 1.16 及更早的 Forge 补 `-DlibraryDirectory`。
3. **log4j2 安全配置**：1.12–1.17 的版本 JSON 带 `logging.client.argument`
   （`-Dlog4j.configurationFile=${path}`）。只有当 `.minecraft/assets/log_configs/<sha1>` 存在时才注入，
   避免把不存在的路径塞给 JVM。
4. **窗口参数只加一次**：`has_custom_resolution` 会从 JSON 带出 `--width/--height`；
   若模板里已有这两个标志，就不再追加用户设置的窗口尺寸。
5. `LaunchPlan` 是**纯数据**，可以打印（CLI `plan` 命令）、复制到剪贴板、写进日志，
   方便排查"为什么启动失败"。

## 7. `GameProcess`：进程托管

```csharp
info.WorkingDirectory = plan.GameDirectory;     // 必须是 .minecraft 根目录
foreach (var a in plan.JvmArguments) info.ArgumentList.Add(a);
info.ArgumentList.Add(plan.MainClass);
foreach (var a in plan.GameArguments) info.ArgumentList.Add(a);
process.OutputDataReceived += ...               // 逐行转发
process.Exited += ...                            // 只触发一次
```

* 用 `ArgumentList`（而不是拼字符串）避免路径含空格时的引号问题；
* `UseShellExecute = false` + 重定向，才能读到日志；
* `Kill(entireProcessTree: true)` 结束时连 Forge 派生的子进程一起清理；
* `RaiseExited` 有"只触发一次"保护：`kill` 与自然退出可能同时到达。

## 8. 校验与下载

`IntegrityChecker.Check` 产出缺失清单，每项都带可下载 URL：

| Kind | 判定 | URL 来源 |
| --- | --- | --- |
| `library` | `libraries/<artifact.path>` 不存在 | `downloads.artifact.url`，缺失时由 maven 坐标兜底 |
| `client-jar` | 继承链里找不到任何 Jar | `downloads.client.url` |
| `asset-index` | `assets/indexes/<id>.json` 不存在 | `assetIndex.url` |
| `asset` | `assets/objects/<hash[0:2]>/<hash>` 不存在 | `https://resources.download.minecraft.net/...` |

`DownloadService` 以 2–8 个并发下载（`ConcurrentQueue` + 固定 worker 数），
先写 `<name>.tiamc-download` 再改名，`IProgress<DownloadProgress>` 回传百分比。
`DownloadSource.BmclApi` 会把 `libraries.minecraft.net` / `resources.download.minecraft.net`
重写到 `bmclapi2.bangbang93.com`，便于国内网络。

## 9. `JavaDetector`

1. `JAVA_HOME`、`PATH`；
2. 常见安装根目录（`C:\Program Files\Java`、Eclipse Adoptium、Zulu、Corretto、`C:\Java`…）
   以及其它启动器自带的运行时（`%APPDATA%\.minecraft\runtime`），递归 3 层找 `bin\java.exe`；
3. 优先读 JDK/JRE 自带的 `release` 文件（`JAVA_VERSION=`、`OS_ARCH=`），比启动子进程快得多；
4. 需要时再执行 `java -version` 解析 `version "17.0.13"` 与 `64-Bit`。

`Filter(all, requiredMajor)` 先精确匹配版本 JSON 里的 `javaVersion.majorVersion`，
匹配不到再放宽为 "≥ 需求"，最后兜底任意一个。

---

## 10. 与 ColorMC 的实现差异（有意为之）

| 方面 | ColorMC | TIA-MC | 原因 |
| --- | --- | --- | --- |
| UI 框架 | Avalonia（跨平台） | WPF（Windows） | 目标界面是 TIA Portal 的 Windows 桌面观感 |
| 依赖 | Newtonsoft.Json、SixLabors、下载器、皮肤渲染等 | 仅 BCL（`System.Text.Json`） | 内核保持可移植、可审计，便于学习 |
| 实例模型 | 每个实例一个独立目录 + `config.json`（`GameSettingObj`） | 直接以 `versions/<id>` 为实例，共享 `.minecraft` | 目标是"能直接接管已有 PCL/官方安装" |
| Forge 支持 | 自行下载 installer 并安装（`DownloadItemHelper.BuildForgeInstaller`） | 只启动已安装好的 Forge（读现成 JSON） | 缩小范围，把重点放在启动流程 |
| 下载 | 自研多线程下载器 + 镜像切换 | `HttpClient` 并发 + BMCLAPI 重写 | 够用且易读 |
| 登录 | 微软 OAuth / Authlib-Injector / 离线 | 仅离线（接口已留出 `userType`/token） | 见 README「已知边界」 |

内核里保留了"账户只提供 `name/uuid/accessToken/userType`"这一抽象，
所以接入 MSA 登录只需要新增一个 `MinecraftAccount` 的构造路径，`LaunchPlanner` 无需改动。
