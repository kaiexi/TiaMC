# 第三方组件与参考项目说明（THIRD-PARTY NOTICES）

TiaMC 的**源代码全部为独立实现**。本文件说明：本项目在学习与实现过程中**参考过哪些开源项目**、
**运行时会让用户下载哪些第三方组件**、以及**本仓库不包含什么**。

> 关于"这些代码是谁写的"：见 [AI-DISCLOSURE.md](AI-DISCLOSURE.md)（由 AI 编程助手在人类指导下生成）。
>
> 结论先行：仓库里没有任何 GPL / Apache 项目的代码副本；被参考的项目只用于阅读理解与思路借鉴，
> 对应的实现由本项目重新编写。运行时下载的第三方组件（Java 运行时、authlib-injector）
> **不随仓库分发**，其许可由各自项目约束使用者。因此本项目可以 **MIT** 许可发布。

## 一览表（谁、什么许可、以什么形式进入本项目）

| 开源项目 | 版权所有者 | 许可 | 进入本项目的形式 |
|----------|-----------|------|------------------|
| [ColorMC](https://github.com/Coloryr/ColorMC) | Coloryr 及贡献者 | Apache-2.0 | **仅设计参考**（无代码/无文本） |
| [HMCL](https://github.com/HMCL-dev/HMCL) | huangyuhui 及贡献者 | GPL-3.0 | **仅设计参考**（无代码；阅读用副本已从仓库移除） |
| [Axolotl](https://github.com/Mystic-Stars/Axolotl) | Mystic-Stars 及贡献者 | 自定义（见其仓库） | **仅格式与产品思路参考**（词典内容为自建；阅读用副本已从仓库移除） |
| [LunaLauncher](https://github.com/AndreaFrederica/LunaLauncher/tree/meson/yggdrasil) | AndreaFrederica 及贡献者 | GPL-3.0 | **仅技术规范参考**（按公开协议自行实现服务端） |
| [PCL2](https://github.com/Meloong-Git/PCL) | Hex-Dragon（LTCat）及贡献者 | 自定义（见其仓库） | **仅交互与下载路线参考** |
| [Mem Reduct](https://github.com/henrypp/memreduct) | henrypp 及贡献者 | GPL-3.0 | **仅 Windows 公开 API 用法参考**（自行实现） |
| [authlib-injector](https://github.com/yushijinhun/authlib-injector) | yushijinhun 及贡献者 | 自定义（见其 COPYING.md） | **运行时下载的独立 jar**，不打包、不修改 |
| [Eclipse Temurin / Adoptium](https://adoptium.net/) | Eclipse Foundation | GPLv2 + Classpath Exception | **运行时下载的 JRE**，不打包、不修改 |
| [BMCLAPI](https://bmclapi2.bangbang93.com/) | bangbang93 及贡献者 | 服务条款 | **联网镜像服务**（不下发代码） |
| [Modrinth API](https://docs.modrinth.com/) | Modrinth | API 使用条款 | **联网接口**（不下发代码） |
| [.NET 10 / WPF](https://dotnet.microsoft.com/) | Microsoft | MIT | 运行框架（NuGet 依赖：仅 `System.Text.Json`） |

> 表中前三列之外的任何文件（游戏资源、Java 运行时、authlib-injector）都**不在本仓库**里，
> 也不在任何 Release 附件里；它们在用户机器上按需下载。

---

## 一、参考过设计思路的开源项目（未复制代码）

| 项目 | 作者 / 仓库 | 许可 | 本项目参考了什么 | 是否复制代码 |
|------|-------------|------|------------------|--------------|
| **ColorMC** | Coloryr · [Coloryr/ColorMC](https://github.com/Coloryr/ColorMC) | **Apache-2.0** | 启动器内核的整体分层思路：版本 JSON 继承合并 → 规则判定 → 库/资源解析 → 占位符替换 → 启动 JVM；账户与下载模块的职责划分 | ❌ 未复制（独立实现，接口与数据结构自定义） |
| **HMCL** | huangyuhui 及贡献者 · [HMCL-dev/HMCL](https://github.com/HMCL-dev/HMCL) | **GPL-3.0** | ① 崩溃报告分析的结构（规则表 + 堆栈包名关键字 + 疑似模组匹配 + class 版本→Java 版本映射）② 内存/垃圾回收设置的呈现方式 ③ Java 运行时管理体验 | ❌ 未复制。仅阅读其 `CrashReportAnalyzer` 等实现后，用 C# 重写为 `src/TiaMc.Core/Diagnostics/CrashAnalyzer.cs`；阅读用的源码副本已从仓库移除 |
| **Axolotl** | Mystic-Stars · [Mystic-Stars/Axolotl](https://github.com/Mystic-Stars/Axolotl) | 自定义（见其仓库） | 内容搜索的产品形态：一个目录承载「模组 / 整合包 / 资源包 / 光影」，中文名检索（`WikiEntries.txt` 格式 `slug@curseforgeId\|中文名 (English)`）、`searcher_words.txt` 关键词扩展 | ❌ 未复制。中文词典为手工编写的 80 条内置表（`ResourceCatalog.ChineseDictionary`），格式与之兼容但内容自建；阅读用的源码/数据副本已从仓库移除 |
| **LunaLauncher** | AndreaFrederica · [AndreaFrederica/LunaLauncher](https://github.com/AndreaFrederica/LunaLauncher/tree/meson/yggdrasil) | **GPL-3.0** | Yggdrasil 服务端实现所遵循的**技术规范**（`.agent/Yggdrasil 服务端技术规范.md`）：13 个端点的路径、请求/响应字段、SHA1withRSA 属性签名、皮肤/披风 `textures` 载荷、`OfflinePlayer:<name>` 离线 UUID 规则 | ❌ 未复制。按规范自行实现 `src/TiaMc.Core/Yggdrasil/*`，并用 13/13 自测脚本验证 |
| **PCL2** | Hex-Dragon / Meloong-Git · [Meloong-Git/PCL](https://github.com/Meloong-Git/PCL) | 自定义（见其仓库） | 「国内镜像优先」的下载路线选择思路（BMCLAPI 优先、官方源回退）与版本列表的交互习惯 | ❌ 未复制 |
| **Mem Reduct** | henrypp · [henrypp/memreduct](https://github.com/henrypp/memreduct) | **GPL-3.0** | 内存回收所用的 **Windows 机制**（公开 API）：`EmptyWorkingSet`、`NtSetSystemInformation(SystemMemoryListInformation)` 的 `MemoryPurgeStandbyList(4)` / `MemoryFlushModifiedList(3)`、`SystemFileCacheInformation`，以及所需的 `SeProfileSingleProcessPrivilege` / `SeIncreaseQuotaPrivilege` 权限 | ❌ 未复制。按公开文档与 API 语义在 `src/TiaMc.Core/Utils/MemoryTrimmer.cs` 重新实现 |

> 说明：Minecraft 启动相关的数据格式（version JSON、assets index、library 规则、arguments 占位符、
> Microsoft 登录链路）来自 **Mojang 官方公开规范**与 [Minecraft Wiki](https://minecraft.wiki/w/Microsoft_authentication)，
> 不属于上述任何项目的私有实现。

---

## 二、运行时下载的第三方组件（不随仓库/发行包分发）

| 组件 | 来源 | 许可 | 触发方式 | 存放位置 |
|------|------|------|----------|----------|
| **Eclipse Temurin JRE**（Java 17 / 21 …） | [Adoptium API](https://api.adoptium.net/) → 清华大学 TUNA 镜像回退 | GPLv2 + Classpath Exception | 版本缺少对应 Java 且开启「缺失时自动下载」时 | `<程序目录>\runtime\java-<N>\` |
| **authlib-injector** | [authlib-injector.yushi.moe](https://authlib-injector.yushi.moe/) → BMCLAPI 镜像 → GitHub 回退 | 自定义（见其 [COPYING.md](https://github.com/yushijinhun/authlib-injector/blob/develop/COPYING.md)） | 使用「外置登录（第三方皮肤站）」账户首次启动游戏时 | `<程序目录>\runtime\authlib-injector.jar` |
| **Minecraft 版本文件 / 库 / 资源** | Mojang 官方源 或 [BMCLAPI](https://bmclapi2.bangbang93.com/) 镜像 | Mojang 最终用户许可 | 下载、补全、启动时 | `<Minecraft 目录>\`（可便携式放在程序目录） |

用户可在属性面板关闭 Java 自动补齐；authlib-injector 仅在外置登录账户启动时下载。

---

## 三、联网访问的第三方服务（仅调用接口，不含代码）

| 服务 | 用途 | 说明 |
|------|------|------|
| Modrinth API (`api.modrinth.com`) | 模组 / 整合包 / 资源包 / 光影搜索与下载 | 遵循其 API 使用条款，请求带 `User-Agent` |
| BMCLAPI (`bmclapi2.bangbang93.com`) | Minecraft 文件镜像 | 感谢 bangbang93 与贡献者 |
| Mojang 官方源 (`piston-meta` / `launchermeta` / `launcher.mojang.com`) | 版本清单、库、资源、官方 Java 运行时清单 | |
| LittleSkin / Blessing Skin 站点 | 外置登录（Yggdrasil）与皮肤 | 由用户自行选择站点 |
| mc-heads.net · Minotar · Crafatar · Visage | 按玩家名 / UUID 获取皮肤图片 | 皮肤站选项之一，结果保存在本机 |
| api.adoptium.net | 查询 JRE 下载地址与 SHA-256 | |

---

## 四、本仓库**不包含**的内容

* ❌ Minecraft 客户端 / 服务端文件、Mojang 资源与资产索引。
* ❌ Java 运行时（JRE/JDK）、authlib-injector（两者均在运行时下载）。
* ❌ 任何 GPL / Apache 项目的源代码副本，包括阅读用的第三方源码（学习笔记中的第三方文件已移除，仅保留本项目的实现）。
* ❌ 用户账户信息（正版令牌、外置登录令牌、皮肤站密码）；配置与日志只写在本机 `config\` 下，导出日志时会对 `accessToken` / `refreshToken` / `clientToken` / `password` / `uuid` 做脱敏。

---

## 五、本项目自身许可

TiaMC 以 **MIT** 许可发布，见 [LICENSE](LICENSE)。

由于参考项目包含 GPL-3.0 代码（HMCL、LunaLauncher、Mem Reduct），请注意：
**不得**把上述 GPL 项目的代码直接复制进本仓库；若需要引用其实现，请自行评估许可兼容性并以 GPL-3.0 发布衍生作品。
