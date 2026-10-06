# 学习笔记：从三个开源启动器学到了什么（仅结论，不含第三方源码）

> 本文件只记录**本项目自己的结论与实现位置**。阅读第三方源码时使用过的文件副本（GPL 项目）已从仓库移除，
> 相关许可与参考范围见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

## 1. HMCL（GPL-3.0）→ 崩溃分析与 Java 管理

* 崩溃分析不该只做关键字匹配，而应分三层：
  1. **规则表**：症状 → 原因 → 处理建议（本项目 17 条，见 `CrashAnalyzer.Rules`）；
  2. **堆栈关键字**：从 `at <包名>` 里提取 mod 包名，与黑名单/已知库前缀比对；
  3. **疑似模组匹配**：把异常里出现的类名/包名与 `mods` 目录里扫描到的 `ModId` 对齐，指出"最可能是哪个模组"。
* `UnsupportedClassVersionError` 要反查 class 文件主版本（52=Java8、61=Java17、65=Java21）并给出所需 Java。
* 内存设置要读取本机物理内存给出建议值，而不是写死。
* 实现：`src/TiaMc.Core/Diagnostics/CrashAnalyzer.cs`、`src/TiaMc.Core/Utils/SystemInfo.cs`。

## 2. Axolotl（自定义许可）→ 内容搜索

* 四类内容（模组 / 整合包 / 资源包 / 光影）应共用**一个目录与一套搜索入口**，只切换 `project_type`。
* 中文检索的正确做法是"翻译层"：中文关键词 → 英文 slug / CurseForge id，再送去搜索；
  词典格式 `slug@curseforgeId|中文名 (English)`，多义项用分隔符并列。
* 搜索结果要能按游戏版本 + 装载器过滤。
* 实现：`src/TiaMc.Core/Resources/ResourceCatalog.cs`（含 80 条内置中文词典 + 可选 `config\cache\search-zh.txt`）。

## 3. ColorMC（Apache-2.0）→ 启动内核分层

* 版本 JSON 的 `inheritsFrom` 要递归合并，且子版本覆盖父版本；
* 规则（rules）判定要考虑 os / arch / features（`has_custom_resolution` 等）；
* natives 要按平台解压，classpath 顺序影响加载；
* 占位符替换要集中在一处完成（`${auth_player_name}`、`${version_name}`、`${assets_index_name}` …）。
* 实现：`src/TiaMc.Core/Minecraft/VersionRepository.cs`、`src/TiaMc.Core/Launch/LaunchPlanner.cs`。

## 4. LunaLauncher（GPL-3.0）→ Yggdrasil 服务端规范

* 需要实现的端点：`/authserver/{authenticate,refresh,validate,invalidate,signout}`、
  `/sessionserver/session/minecraft/{join,hasJoined,profile/<uuid>}`、`/api/profiles/minecraft`、
  `/api/user/profile/<uuid>/<skin|cape>`、`/textures/<hash>`、根路径元数据；
* `feature.non_email_login` 允许非邮箱用户名；
* 材质属性要用 SHA1withRSA 签名后 base64；
* 离线 UUID 用 `OfflinePlayer:<name>` 的 MD5（带版本位）。
* 实现：`src/TiaMc.Core/Yggdrasil/*`，13/13 自测：`tiamc-cli yggdrasil --port 25567`。

## 5. Mem Reduct（GPL-3.0）→ 内存回收

* `EmptyWorkingSet` 回收进程工作集；
* `NtSetSystemInformation(SystemMemoryListInformation = 0x50)` 配 `MemoryPurgeStandbyList(4)` 清理待机列表，`MemoryFlushModifiedList(3)` 刷已修改页；
* `SystemFileCacheInformation = 0x15` 处理文件缓存；
* 需要 `SeProfileSingleProcessPrivilege` / `SeIncreaseQuotaPrivilege`（因此启动器提供"以管理员身份重启"，但不强制 requireAdministrator）。
* 实现：`src/TiaMc.Core/Utils/MemoryTrimmer.cs`。