# TiaMC 的 IE6 兼容说明

本文件说明"内置 IE6 / 兼容 IE6"这件事到底怎么做、做到什么程度，以及**为什么不能把 IE6 内核塞进安装包**。

## 一、先说结论

| 需求 | 现实情况 | 本项目的做法 |
|------|----------|--------------|
| 把 IE6 内核（mshtml.dll 6.0）打包进程序 | ❌ 做不到：IE6 是 2001 年随 Windows XP/2000 发布的**系统组件**，依赖 XP 时代系统 DLL，不可再分发，也无法在 Windows 10/11 上注册运行 | 不打包内核 |
| 在没有虚拟机的前提下看到 IE6 级排版 | ⚠️ 只能用系统仍保留的 **MSHTML/Trident** 引擎的兼容文档模式 | 内置浏览器窗口把文档模式设为 **5000（IE5 quirks）**，这是 IE6/IE5.5 当年的排版行为；窗口与菜单用系统原生控件，不做外观模仿 |
| Flash 内容（老页游 / SWF） | ⚠️ Flash 插件 2020-12-31 EOL，现代浏览器不再加载 | ① **Ruffle**（开源 Flash 模拟器，运行时下载）在页面内直接播 SWF；② 本机若有 `flashplayer_*.exe`（Flash 投影播放器）可直接调用 |
| 在**真正的 IE6** 里打开本启动器的界面 | ✅ 可行 | 页面提供**只用 table/float + filter + ES3** 的 IE6 专用版（`/legacy` + `ie6.css` + `legacy.js`），并支持 `--host 0.0.0.0` 把服务开到局域网，让另一台跑真 IE6 的机器（XP 物理机 / 别人的机器）直接访问 |

一句话：**界面本身做到真 IE6 可渲染**，引擎则用系统保留的 Trident 兼容模式；要"绝对真"的 IE6，就在局域网里用一台真 IE6 机器打开这个页面（启动器会打印出可访问的局域网地址）。

## 二、IE6 兼容是怎么写出来的

参考了 [ManfredHu/Win10ProductPage](https://github.com/ManfredHu/Win10ProductPage)（"Win10 产品页面，兼容 IE6"）的做法，并结合 IE6 的已知限制：

| IE6 的限制 | 本项目的写法 |
|------------|--------------|
| 不支持 flex / grid / `min-height` / `calc()` / `border-radius` / `box-shadow` | 布局全部用 **table + float + 定宽**；等高用固定 `height` + `overflow`；圆角/渐变/阴影改用 **`filter: progid:DXImageTransform.Microsoft.gradient(...)`**（现代浏览器直接忽略） |
| 只支持 `a:hover`，其它元素没有 `:hover` | 悬停只写在 `a:hover` 上；按钮状态靠 `input.xp` 的边框与底色 |
| 浮动元素双倍外边距 bug | 浮动元素统一 `display: inline` |
| 缺 hasLayout 导致各种怪异表现 | 需要的地方加 `zoom: 1`（含 `fieldset`、工具栏、页脚） |
| PNG 透明通道不认 | 提供 `.pngfix` 类，用 `AlphaImageLoader` + `#default#VML` 兜底 |
| `<button>` 样式支持差、内边距被吞 | IE6 页面统一用 `<input type="button" class="xp">` 并 `overflow: visible` |
| 没有 `JSON`、`fetch`、`addEventListener`、`const/let`、箭头函数 | `legacy.js` 用 **ES3**：`var`、`function`、`attachEvent`、`XMLHttpRequest`（含 `ActiveXObject` 回退）、`eval('(' + text + ')')` 解析 JSON |
| `X-UA-Compatible` 与文档模式 | `/legacy` 页面带 `<meta http-equiv="X-UA-Compatible" content="IE=5">`；内置窗口通过注册表 `FEATURE_BROWSER_EMULATION` 指定模式 |
| 表格必须写 `cellspacing`/`cellpadding` | 表格统一 `cellspacing="0"`，边框交给 `ie6.css` |

## 三、关于「利用世界之窗里面的内核」（实测结论）

世界之窗（TheWorld）3.6 安装目录里**只有 `TheWorld.exe`（2.1 MB 外壳）**，没有任何
`mshtml.dll` / `ieframe.dll` / `shdocvw.dll`：它是**纯 IE 外壳**，IE 模式使用的就是系统 IE。
本机实测：

| 程序 | 安装位置 | 自带引擎文件 | IE 模式实际引擎 |
|------|----------|--------------|------------------|
| 世界之窗 TheWorld 3.6.1.1 | `C:\Program Files (x86)\TheWorld 3` | ❌ 无 mshtml.dll | 系统 IE 11.00.26100 |
| 系统 Internet Explorer | `C:\Program Files\Internet Explorer` | — | Trident 11.00.26100 |
| 系统 IE（32 位） | `C:\Program Files (x86)\Internet Explorer` | — | SysWOW64 Trident 11.00.26100 |
| 傲游 / 360se / 腾讯 TT / GreenBrowser | — | ❌ 均为 IE 外壳 | 系统 IE |

所以"世界之窗里的内核"= 系统 IE 11，拿不到 IE6。本项目把这类浏览器做成了**浏览器通道**：
`/api/browsers` 列出本机识别到的通道并**如实标注各自引擎版本**，一键用它们打开页面
（实测：用世界之窗 3.6 成功打开 `http://127.0.0.1:<端口>/legacy`，标题显示 "TIA-MC 启动器（IE6 兼容页） - 世界之窗 3.6"）。

## 四、真 IE6 引擎到底从哪来

| 来源 | 结果 |
|------|------|
| `ie6setup.exe`（微软签名，6.00.2800.1106，472 KB） | ❌ 只是在线安装存根：内含 `ie6wzd.exe` / `wininet.dll` / `urlopen` / `iesetup.inf`，**没有** mshtml.dll 与 iexplore.exe；下载服务器早已下线 |
| 世界之窗 / 360 / 傲游 / TT 等国产外壳 | ❌ 不含引擎（见上表） |
| 全盘扫描私有 `mshtml.dll` 副本 | ❌ 本机未发现（只有 System32 / SysWOW64 的 11.x） |
| GitHub [oldweb-today/wine-browsers](https://github.com/oldweb-today/wine-browsers)（`ie6/run.sh`） | ✅ **可行**：Wine 前缀 + 真 IE6，`wine start /max /W 'C:/Program Files/Internet Explorer/IEXPLORE.exe' $URL`；Windows 上通过 **WSL2 + WSLg** 跑（不是虚拟机），启动器提供一键通道与准备命令 |
| 已装好 IE6 的 XP 物理机（局域网） | ✅ 启动器 `--host 0.0.0.0` 打印局域网地址，IE6 直接开 `/legacy` |
| 从 XP 机器复制整份 `Internet Explorer` 目录 | ⚠️ 可放进 `ie6\` 引擎槽，但 IE6 的 mshtml 6.0 依赖 XP 系统栈，在新系统上**大概率加载失败**（启动器会如实报错） |


### 实测：XP 的 IEXPLORE.EXE 6.00.2900.5512 在 Windows 11 上能不能跑

拿一份**真的** IE6 外壳（`C:\Users\3214\Desktop\Internet Explorer\IEXPLORE.EXE`，
版本 `6.00.2900.5512 (xpsp.080413-2105)`）在本机（Windows 11 26100）实测三种兼容手段：

| 手段 | 命令/做法 | 结果 |
|------|-----------|------|
| 直接启动 | `Start-Process IEXPLORE.EXE about:blank` | ❌ 退出码 **0xC0000142**（STATUS_DLL_INIT_FAILED） |
| XP 兼容层 | `__COMPAT_LAYER=WINXPSP3` | ❌ 同样 0xC0000142 |
| DLL 重定向 | 建 `IEXPLORE.EXE.local` 让同目录 DLL 优先 | ❌ 同样 0xC0000142 |

原因（可复核）：

* 该目录里**只有外壳**：`IEXPLORE.EXE` + `HMMAPI.DLL` + `iedw.exe`；缺少 **18 个引擎文件**
  （`mshtml.dll`、`shdocvw.dll`、`urlmon.dll`、`wininet.dll`、`browseui.dll`、`inseng.dll`、`mlang.dll`、
  `cdfview.dll`、`danim.dll`、`dxtmsft.dll`、`dxtrans.dll`、`iedkcs32.dll`、`iepeers.dll`、`imgutil.dll`、
  `occache.dll`、`webcheck.dll`、`iecont.dll`、`msrating.dll`）。
* IE6 外壳按名字绑定 `mshtml/shdocvw/urlmon/wininet` 等，Win11 上这些解析到 **IE11 版本**
  （`SysWOW64\mshtml.dll 11.00.26100`、`shdocvw 10.0.26100`），COM 接口与导出都不兼容。
* 即使把 XP 的整套 IE6 DLL 拷进来，`kernel32/user32/ole32/shlwapi` 等受 **KnownDLLs / WRP** 约束
  仍会加载系统版本，XP 时代的调用点在新版 DLL 上不存在 → 初始化失败。

### 复测：把你自己的 XP 虚拟机镜像里的真引擎补齐后

你机器上有 XP 虚拟机（`Windows XP Professional-s001.vmdk`，twoGbMaxExtentSparse 分卷）。
**7z / NanaZip 能直接读 VMDK**，所以不必启动虚拟机、也不需要来宾密码就能取到真引擎——
启动器已内置这个动作（`POST /api/ie6/extract`，参数 `image` + `target`）：

```powershell
7z x "Windows XP Professional.vmdk" -o<引擎目录> WINDOWS\system32\mshtml.dll WINDOWS\system32\shdocvw.dll ...
```

实测取到 18 个文件，全部 `6.00.2900.5512 (xpsp.080413-2105)`：
`mshtml.dll` 2,994 KB、`shdocvw.dll` 1,464 KB、`danim.dll` 1,024 KB、`browseui.dll` 1,000 KB、
`wininet.dll` 636 KB、`urlmon.dll` 598 KB、`mlang.dll` 572 KB、`dxtmsft.dll` 350 KB、`iedkcs32.dll` 316 KB、
`webcheck.dll` 260 KB、`iepeers.dll` 245 KB、`dxtrans.dll` 200 KB、`cdfview.dll` 146 KB、`msrating.dll` 143 KB、
`inseng.dll` 94 KB、`occache.dll` 92 KB、`imgutil.dll` 35 KB、`IEXPLORE.EXE` 91 KB。
（`iecont.dll` 与 `hmmapi.dll` 在 XP SP3 里本就不存在/不参与渲染。）

补齐后再测（`.local` DLL 重定向 + `__COMPAT_LAYER=WINXPSP3`）：

| 状态 | 实测结果 |
|------|----------|
| 只有外壳（缺 18 个 DLL） | ❌ 进程秒退 `0xC0000142`（STATUS_DLL_INIT_FAILED） |
| **补齐 17 个 XP 引擎 DLL** | ⚠️ 进程**不再秒退**（8 秒后仍存活），但 `MainWindowHandle = 0`：**不创建窗口**；CPU 时间 0.03 s、1 线程，**也没有加载 mshtml/shdocvw** —— 卡在启动早期 |
| 加载模块检查 | ❌ 进程里看不到 `mshtml.dll` / `shdocvw.dll` / `urlmon.dll` 等任何 IE 引擎模块 |

原因：IE6 的框架窗口依赖**它自己的 COM/注册表注册**（类工厂指向 XP 路径，加上 XP 时代的
`HKLM\...\Internet Explorer` 状态）。这些注册在 Win11 上受 **WRP** 保护无法写入；即使用户级 CLSID
劫持绕过，也会破坏系统的 IE/WebBrowser 引擎——而本启动器的内置浏览器窗口正是用它。

**结论：Windows 11 上拿不到可用的 IE6 窗口。** 启动器把这套流程做成了「从 XP 镜像提取引擎」+「IE6 兼容启动」，
并把真实结果（退出码含义、缺失文件、无窗口的实际情况）直接显示在界面上。

**因此：Windows 11 上无法直接运行 IE6 内核。** 启动器把这三种尝试做成了「IE6 兼容启动」按钮，
并把退出码含义、缺失文件清单、替代方案直接显示在界面上（不让使用者猜）。

### 关于"从泄露的 XP 源码构建 IE6 内核"（明确不做）

网上（例如 GitHub 上的某些仓库）存在包含 **Windows XP 泄露源码树** 的项目（目录形如
`winsdk/public/sdk/inc/…`、`winsdk/shell/…`）。本项目**不会使用它**，原因有两条，且都很硬：

1. **法律**：那份源码是微软被窃取的专有代码。编译、分发其产物（哪怕只是 mshtml.dll）属于侵权，
   也会让本仓库（MIT）直接变成"分发侵权物"的项目。
2. **技术**：那些仓库里是**源码**（.h/.c/.cpp），不是可运行的 IE6。构建 mshtml 6.0 需要 XP 时代的
   私有构建系统与内部工具链；即使构建成功，mshtml 6 依赖 XP 的系统栈（urlmon/shlwapi/ole32 等同期版本），
   在新版 Windows 上无法注册加载（同名 mshtml.dll 已被 IE11 占用，IE 一个系统只能有一套）。

**合法且可行的替代**（本项目已实现）：
* 你自己的**已授权 XP 环境**里复制一份运行正常的 IE6 目录 → 丢进 `ie6\` 引擎槽；
* **WSL + Wine**：`winetricks -q ie6` 从微软遗留镜像安装真 IE6（启动器提供一键通道与准备命令）；
* **局域网里的真 IE6 机器**：`--host 0.0.0.0` 打开服务，用 IE6 访问 `/legacy`。
## 五、如何自己验证

```powershell
# 1) IE6 兼容性静态检查（扫描服务端页面与样式里的 IE6 不兼容写法）
powershell -File tools\check-ie6.ps1

# 2) 启动 Web 版：内置浏览器窗口（IE5 quirks 文档模式）
dotnet run --project src\TiaMc.Web

# 3) 让真 IE6 机器访问（把服务开到局域网，启动器会在日志里打印地址）
dotnet run --project src\TiaMc.Web -- --host 0.0.0.0
#   然后在 XP/2000 的 IE6 地址栏输入  http://<本机局域网IP>:32123/legacy

# 4) 切换到 IE11 文档模式（现代页面 /）
dotnet run --project src\TiaMc.Web -- --ie11-mode --browser=default
```

`tools\check-ie6.ps1` 会逐项检查：`flex`、`grid`、`min-height`、`calc(`、`border-radius`、`box-shadow`、`rgba(`、`opacity`、`transform`、`position:fixed`、非 `a` 元素的 `:hover`、ES6 语法（`const`/`let`/`=>`/`fetch`/`addEventListener`）等，并输出 PASS/FAIL 列表。

## 六、实现位置

| 文件 | 作用 |
|------|------|
| `src/TiaMc.Web/EmbeddedBrowserWindow.cs` | 内置浏览器窗口（系统原生窗口 + WinForms `WebBrowser` = 系统 MSHTML/Trident），设置文档模式、菜单里可查引擎信息 |
| `src/TiaMc.Web/wwwroot/legacy.html` + `legacy.js` + `ie6.css` | IE6 专用页面：表格布局、无 CSS3、ES3 脚本 |
| `src/TiaMc.Web/wwwroot/index.html` + `app.js` + `app.css` | 现代页面（Edge/Chrome），XP 风格字体与配色 |
| `src/TiaMc.Web/FlashService.cs` | Ruffle 下载/托管、SWF 库、投影播放器调用 |
| `src/TiaMc.Web/Program.cs` | 本机 HTTP 服务与 `/api/*`、静态资源、`--host/--port/--config/--ie6-quirks/--ie11-mode/--browser` 参数 |

## 七、许可与来源

* 参考项目：**ManfredHu/Win10ProductPage**（仓库未声明许可，本项目**未复制其任何代码或图片**，仅采用其"面向 IE6 的写法"这一公开思路：table/float 布局、`filter` 渐变、浮动的 `display:inline`、hasLayout `zoom:1`）。
* **Ruffle**（Flash 模拟器）MIT / Apache-2.0，运行时下载，不随包分发，见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。
* IE / MSHTML / Trident 是 Microsoft 的系统组件，本项目只调用系统已有实现，不打包、不修改。
