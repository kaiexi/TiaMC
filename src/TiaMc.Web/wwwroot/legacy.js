/* =========================================================================
   IE6 兼容页脚本：ES3 语法 + XMLHttpRequest + document.all 风格，
   专门给内置 IE6 窗口（系统 MSHTML/Trident，可运行在 IE5 quirks 模式）使用。
   ========================================================================= */

var state = { remote: [], instance: '', logCursor: 0, settings: null, ruffleTried: false };

function $(id) { return document.getElementById(id); }
function setText(id, value) { var e = $(id); if (e) e.innerHTML = value; }

function esc(value) {
  if (value === null || value === undefined) value = '';
  return String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;')
    .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function status(text, detail) {
  setText('status-main', esc(text));
  if (detail !== undefined) setText('status-detail', esc(detail));
}

/* ------------------------------------------------------- HTTP（ES3） */

function xmlhttp() {
  try { return new XMLHttpRequest(); } catch (e) {}
  try { return new ActiveXObject('Msxml2.XMLHTTP'); } catch (e) {}
  try { return new ActiveXObject('Microsoft.XMLHTTP'); } catch (e) {}
  return null;
}

/* IE5 quirks 文档模式下**没有 JSON 对象**（实测 "JSON"未定义），
   所以这里自己把对象拼成表单编码 a=1&b=2；服务端两种都收。 */
function formEncode(body) {
  if (!body) return '';
  var parts = [];
  for (var key in body) {
    if (!body.hasOwnProperty(key)) continue;
    if (body[key] === null || body[key] === undefined) continue;
    parts[parts.length] = encodeURIComponent(key) + '=' + encodeURIComponent(String(body[key]));
  }
  return parts.join('&');
}

function req(method, path, body, done) {
  var xhr = xmlhttp();
  if (!xhr) { status('浏览器不支持 XMLHttpRequest', path); return; }
  xhr.open(method, path, true);
  var payload = null;
  if (body) {
    payload = formEncode(body);
    xhr.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded');
  }
  xhr.onreadystatechange = function () {
    if (xhr.readyState !== 4) return;
    var data;
    var text = xhr.responseText || '';
    try { data = eval('(' + text + ')'); }
    catch (e) { data = { ok: false, message: text }; }
    if (done) done(data);
  };
  try { xhr.send(payload); }
  catch (e2) { status('请求发送失败', String(e2.message || e2)); }
}

function api(path, done) { req('GET', path, null, done); }
function post(path, body, done) { req('POST', path, body || {}, done); }

/* ------------------------------------------------------- 渲染 */

function refreshAll() {
  status('正在获取启动器状态…', '');
  api('/api/state', function (d) {
    setText('ov-instances', esc(d.instances || 0));
    setText('ov-active', '当前：' + esc(d.activeInstance || '未选择'));
    setText('ov-accounts', esc(d.accounts || 0));
    setText('ov-account', '当前：' + esc(d.selectedAccount || '无'));
    setText('ov-java', esc(d.java || 0));
    setText('ov-flash', d.flash && d.flash.ruffleReady ? 'Ruffle 就绪' : '未安装');
    setText('ov-flash-note', d.flash && d.flash.projectorFound ? '已检测到投影播放器' : 'Ruffle / 投影播放器');
    setText('root-path', esc(d.root || ''));
    state.instance = d.activeInstance || state.instance;
    status('完成', d.root || '');
  });
  loadVersions();
  loadAccounts();
  loadMods();
  loadFlash();
}

function loadVersions() {
  api('/api/versions', function (d) {
    var table = $('installed-table');
    var i;
    while (table.rows.length > 1) table.deleteRow(1);
    var installed = d.installed || [];
    for (i = 0; i < installed.length; i++) {
      var row = table.insertRow(-1);
      row.insertCell(-1).innerHTML = esc(installed[i].id);
      row.insertCell(-1).innerHTML = esc(installed[i].loader);
      row.insertCell(-1).innerHTML = 'Java ' + esc(installed[i].javaMajor);
      row.insertCell(-1).innerHTML = '<button class="xp" onclick="launchInstance(\'' +
        esc(installed[i].id) + '\')">启动</button>';
    }
    if (!installed.length) {
      var empty = table.insertRow(-1);
      empty.insertCell(-1).innerHTML = '还没有实例：请先点「获取版本清单」，再到在线版本里安装。';
      empty.cells[0].colSpan = 4;
    }

    state.remote = d.remote || [];
    renderRemote();
    status('完成', installed.length + ' 个实例 / ' + (d.remoteTotal || 0) + ' 个在线版本');
  });
}

function renderRemote() {
  var filter = $('remote-filter').value;
  var search = $('remote-search').value.toLowerCase();
  var table = $('remote-table');
  while (table.rows.length > 1) table.deleteRow(1);

  var shown = 0, total = 0;
  for (var i = 0; i < state.remote.length && shown < 300; i++) {
    var v = state.remote[i];
    var type = (v.type || '').toLowerCase();
    if (filter === 'release' && type !== 'release') continue;
    if (filter === 'snapshot' && type !== 'snapshot') continue;
    if (filter === 'old' && type.indexOf('old_') !== 0) continue;
    if (search && (v.id || '').toLowerCase().indexOf(search) < 0) continue;
    total++;
    var label = type === 'release' ? '正式版' : type === 'snapshot' ? '快照（测试版）'
      : type === 'old_beta' ? '旧版 Beta' : type === 'old_alpha' ? '旧版 Alpha' : v.type;
    var row = table.insertRow(-1);
    if (type === 'snapshot' || type.indexOf('old_') === 0) row.className = 'snapshot';
    row.insertCell(-1).innerHTML = esc(v.id);
    row.insertCell(-1).innerHTML = esc(label);
    row.insertCell(-1).innerHTML = esc((v.releaseTime || '').substring(0, 10));
    row.insertCell(-1).innerHTML = '<button class="xp" onclick="installVersion(\'' + esc(v.id) + '\')">安装</button>';
    shown++;
  }
  setText('remote-count', '（显示 ' + shown + ' / 共 ' + total + ' 个）');
}

function loadAccounts() {
  api('/api/accounts', function (d) {
    var table = $('accounts-table');
    while (table.rows.length > 1) table.deleteRow(1);
    var list = d.accounts || [];
    for (var i = 0; i < list.length; i++) {
      var a = list[i];
      var row = table.insertRow(-1);
      if (a.selected) row.className = 'selected';
      row.insertCell(-1).innerHTML = esc(a.name) + (a.selected ? ' （当前）' : '');
      row.insertCell(-1).innerHTML = esc(a.kind);
      row.insertCell(-1).innerHTML = esc(a.uuid);
      row.insertCell(-1).innerHTML = a.selected ? '' :
        '<button class="xp" onclick="selectAccount(\'' + esc(a.key) + '\')">切换</button>';
    }
  });
}

function loadMods() {
  api('/api/mods?instance=' + encodeURIComponent(state.instance), function (d) {
    var table = $('mods-table');
    while (table.rows.length > 1) table.deleteRow(1);
    var list = d.mods || [];
    for (var i = 0; i < list.length; i++) {
      var m = list[i];
      var row = table.insertRow(-1);
      row.insertCell(-1).innerHTML = esc(m.name);
      row.insertCell(-1).innerHTML = esc(m.version);
      row.insertCell(-1).innerHTML = esc(m.loader);
      row.insertCell(-1).innerHTML = esc(m.file);
      row.insertCell(-1).innerHTML = m.enabled ? '已启用' : '已停用';
      row.insertCell(-1).innerHTML = '<button class="xp" onclick="toggleMod(\'' + esc(m.file) + '\')">' +
        (m.enabled ? '停用' : '启用') + '</button>';
    }
    if (!list.length) {
      var empty = table.insertRow(-1);
      empty.insertCell(-1).innerHTML = '该实例没有模组。';
      empty.cells[0].colSpan = 6;
    }
  });
}

function loadFlash() {
  api('/api/flash', function (d) {
    setText('flash-ruffle', d.ruffleReady ? '已就绪（' + esc(d.ruffleVersion || 'nightly') + '）'
      : '未下载：点右边按钮获取');
    setText('flash-projector', d.projectorFound ? esc(d.projector) : '未找到 flashplayer_*.exe');
    setText('flash-count', (d.swfs || []).length + ' 个 SWF');
    setText('flash-dir', esc(d.swfDirectory || ''));

    var table = $('flash-table');
    while (table.rows.length > 1) table.deleteRow(1);
    var list = d.swfs || [];
    for (var i = 0; i < list.length; i++) {
      var row = table.insertRow(-1);
      row.insertCell(-1).innerHTML = esc(list[i].name);
      row.insertCell(-1).innerHTML = esc(list[i].size);
      row.insertCell(-1).innerHTML = esc(list[i].modified);
      row.insertCell(-1).innerHTML = '<button class="xp" onclick="playSwf(\'' + esc(list[i].name) + '\')">播放</button>';
    }
  });
}

function logClass(line) {
  if (line.indexOf('[GAME]') >= 0) return 'log-game';
  if (line.indexOf('[WARN]') >= 0) return 'log-warn';
  if (line.indexOf('[ERR]') >= 0) return 'log-err';
  if (line.indexOf('[\u52a8\u4f5c]') >= 0) return 'log-action';
  if (line.indexOf('[OK]') >= 0) return 'log-ok';
  return 'log-info';
}

var consoleCursor = 0;

function pollGameConsole() {
  api('/api/gameconsole?since=' + consoleCursor, function (d) {
    consoleCursor = d.next || 0;
    var lines = d.lines || [];
    var box = $('game-box');
    if (box && lines.length) {
      var html = '';
      for (var i = 0; i < lines.length; i++) {
        var cls = 'log-game';
        if (lines[i].indexOf('Exception') >= 0 || lines[i].indexOf('ERROR') >= 0) cls = 'log-err';
        else if (lines[i].indexOf('WARN') >= 0) cls = 'log-warn';
        html += '<span class="' + cls + '">' + esc(lines[i]) + '</span>\n';
      }
      box.innerHTML += html;
      box.scrollTop = box.scrollHeight;
    }
    setText('gc-status', d.running ? ('运行中 PID ' + (d.pid || '')) :
      (d.exitCode === null || d.exitCode === undefined ? '未启动' :
        ('已退出 code=' + d.exitCode + (d.exitCode === 0 ? '（正常）' : ' ← 有问题，点「问题诊断」'))));
  });
}

function clearConsole() {
  consoleCursor = 0;
  $('game-box').innerHTML = '';
  setText('game-diag', '还没有诊断结果。');
  status('控制台视图已清空', '');
}

function gameDiagnose() {
  status('正在分析启动日志与崩溃报告…', '');
  api('/api/diagnose', function (d) {
    var text = '严重程度: ' + (d.severity || '未知') + '\n' + (d.summary || '') + '\n';
    if (d.hints && d.hints.length) {
      text += '\n处理建议:\n';
      for (var i = 0; i < d.hints.length; i++) text += '  · ' + d.hints[i] + '\n';
    }
    if (d.keywords && d.keywords.length) text += '\n命中关键字: ' + d.keywords.join(', ') + '\n';
    setText('game-diag', esc(text).replace(/\n/g, '<br>'));
    status('诊断完成', '结论见下方「问题诊断」框');
  });
}

function pollLogs() {
  api('/api/logs?since=' + state.logCursor, function (d) {
    state.logCursor = d.next || 0;
    var lines = d.lines || [];
    var html = '';
    for (var i = 0; i < lines.length; i++) {
      html += '<span class="' + logClass(lines[i]) + '">' + esc(lines[i]) + '</span>\n';
    }
    var box = $('log-box');
    if (lines.length) {
      box.innerHTML += html;
      box.scrollTop = box.scrollHeight;
    }
    setText('log-count', '共 ' + (d.total || 0) + ' 条，已显示 ' + state.logCursor);
    updateSiteInfo(d.total || 0);
  });
}

function clearLogView() {
  updateSiteInfo(0); $('log-box').innerHTML = ''; state.logCursor = 0; }

/* ------------------------------------------------------- 动作 */

function repairFiles() {
  status('正在校验并补齐文件…', '');
  post('/api/instance/repair', { id: state.instance || '' }, function (d) {
    status(d.ok ? '补齐完成' : '补齐未完成', d.message || '');
    refreshAll();
  });
}

function launch() { launchInstance(state.instance); }

function launchInstance(id) {
  status('正在启动 ' + id + '…', '');
  post('/api/launch', { instance: id }, function (d) {
    setText('plan-box', esc((d.message || '') + '\n\nJava: ' + (d.java || '') + '\n\n' + (d.command || '')));
    status(d.ok ? '游戏已启动' : '启动失败', d.message || '');
  });
}

function shutdownAll() {
  status('正在结束游戏并关闭界面…', '');
  post('/api/stop', {}, function () {
    post('/api/shutdown', {}, function (d) {
      status('界面已关闭', d.message || '');
      try {
        window.open('', '_self'); window.close();
      } catch (e) { }
      setText('status-main', '界面已关闭，可以直接关掉这个窗口');
    });
  });
}

function stopGame() { post('/api/stop', {}, function (d) { status('完成', d.message || ''); }); }

function getManifest() {
  status('正在获取版本清单…', '');
  post('/api/manifest/refresh', {}, function (d) {
    status(d.ok ? '完成' : '失败', '在线版本 ' + (d.versions || 0) + ' 个');
    loadVersions();
  });
}

function installVersion(id) {
  status('正在安装 ' + id + '…', '可能需要几分钟');
  post('/api/instance/install', { id: id }, function (d) {
    status(d.ok ? '安装完成' : '安装失败', d.message || '');
    loadVersions();
  });
}

function addAccount() {
  post('/api/accounts/offline', { name: $('account-name').value || 'Steve' }, function (d) {
    status(d.ok ? '完成' : '失败', d.message || '');
    loadAccounts();
    refreshAll();
  });
}

function selectAccount(key) {
  post('/api/accounts/select', { key: key }, function (d) {
    status(d.ok ? '完成' : '失败', d.message || '');
    loadAccounts();
  });
}

function toggleMod(file) {
  post('/api/mods/toggle', { instance: state.instance, file: file }, function (d) {
    status(d.ok ? '完成' : '失败', d.message || '');
    loadMods();
  });
}

function diagnose() {
  status('正在分析日志与崩溃报告…', '');
  post('/api/diagnose', {}, function (d) {
    setText('diag-box', esc('严重程度: ' + (d.severity || '') + '\n' + (d.summary || '') +
      '\n\n建议:\n- ' + (d.hints || []).join('\n- ') +
      '\n\n疑似模组: ' + ((d.suspects || []).join(', ') || '无')));
    status('完成', '诊断结束');
  });
}

function getRuffle() {
  status('正在从 GitHub 下载 Ruffle…', '');
  post('/api/flash/ruffle', {}, function (d) {
    status(d.ok ? '完成' : '失败', d.message || '');
    loadFlash();
  });
}

function openProjector() {
  post('/api/flash/projector', {}, function (d) { status(d.ok ? '完成' : '失败', d.message || ''); });
}

function playUrl() {
  var url = $('flash-url').value;
  if (!url) return;
  playSwf(url, true);
}

function loadRuffleOnce() {
  // Ruffle 是现代 JS + WASM，IE6/IE5-quirks 解析不了它，
  // 所以绝不能在页面加载时就引入（那会让 IE 弹"脚本错误"）。
  // 只有用户真的要点播放时才尝试加载，并且失败要能说清楚。
  if (state.ruffleTried) return;
  state.ruffleTried = true;
  try {
    var s = document.createElement('script');
    s.src = '/flash/ruffle.js';
    s.onerror = function () { status('Ruffle 加载失败', 'IE6 内核无法运行 Ruffle，请改用投影播放器'); };
    document.body.appendChild(s);
  } catch (e) {
    status('Ruffle 无法注入', '请改用投影播放器（原版 Flash 播放器）');
  }
}

function playSwf(name, isUrl) {
  var url = isUrl ? name : '/flash/swf/' + encodeURIComponent(name);
  loadRuffleOnce();
  $('flash-stage').innerHTML =
    '<object classid="clsid:D27CDB6E-AE6D-11cf-96B8-444553540000" width="100%" height="340">' +
    '<param name="movie" value="' + esc(url) + '">' +
    '<param name="quality" value="high">' +
    '<embed src="' + esc(url) + '" type="application/x-shockwave-flash" width="100%" height="340" quality="high">' +
    '</object>';
  status('Flash 已提交播放', 'IE6 内核下请用投影播放器；Ruffle 需要较新的内核');
}

function projectorUrl() {
  post('/api/flash/open', { target: $('flash-url').value }, function (d) {
    status(d.ok ? '完成' : '失败', d.message || '');
  });
}

function updateSiteInfo(total) {
  var el = $("site-hits");
  if (el) el.innerHTML = "" + total;
  var el2 = $("site-hits2");
  if (el2) el2.innerHTML = "000" + total;
  var up = $("site-updated");
  if (up) {
    var d = new Date();
    up.innerHTML = d.getFullYear() + "-" + pad2(d.getMonth() + 1) + "-" + pad2(d.getDate()) +
      " " + pad2(d.getHours()) + ":" + pad2(d.getMinutes());
  }
}

function pad2(n) { return n < 10 ? ("0" + n) : ("" + n); }

function loadIe6() {
  api("/api/ie6", function (d) {
    setText("ie6-engine", d.engineFound
      ? ("已检测到引擎：版本 " + esc(d.engineVersion) + "（" + esc(d.enginePath) + "）")
      : ("未检测到真 IE6 引擎目录。把含 iexplore.exe + mshtml.dll 的目录放到 " + esc(d.engineDirectory)));
    setText("ie6-host", "进程 " + esc(d.processBits) + " 位；宿主引擎 " + esc(d.hostedEngineDll) +
      "（版本 " + esc(d.hostedEngineVersion) + "，真 IE6=" + esc(d.hostedIsIe6) + "）");
    if (d.missingEngineFiles && d.missingEngineFiles.length > 0) {
      setText("ie6-engine", $("ie6-engine").innerHTML + "；缺少 " + d.missingEngineFiles.length +
        " 个引擎文件（" + d.missingEngineFiles.slice(0, 6).join(", ") + " …）");
    }
    setText("ie6-note", d.wsl
      ? ("WSL: " + (d.wsl.wslPresent ? "已安装" : "未安装") +
         "；发行版 " + (d.wsl.defaultDistro || "无") +
         "；wine " + (d.wsl.wineReady ? "已装" : "未装"))
      : "");
  });
}

function loadBrowsers() {
  api("/api/browsers", function (d) {
    var html = "<b>浏览器通道</b>（点按钮用对应浏览器打开本页）：<br>";
    var list = d.channels || [];
    for (var i = 0; i < list.length; i++) {
      html += "<input type=\"button\" class=\"xp\" value=\"" + esc(list[i].name) +
        "\" onclick=\"openWith(&quot;" + esc(list[i].id) + "&quot;)\">" +
        " <span class=\"gray\">" + esc(list[i].engine) + "</span><br>";
    }
    html += "<div class=\"gray\" style=\"margin-top:4px\">" + esc(d.note || "") + "</div>";
    setText("ie6-channels", html);
  });
}

function openWith(id) {
  post("/api/browsers/launch", { id: id, url: location.href }, function (d) {
    status(d.ok ? "完成" : "失败", d.message || "");
  });
}

function openEngineDir() {
  post("/api/ie6/open-directory", {}, function (d) { status(d.ok ? "完成" : "失败", d.message || ""); });
}

function launchRealIe6() {
  post("/api/ie6/launch", { url: location.href }, function (d) { status(d.ok ? "完成" : "失败", d.message || ""); });
}

function compatLaunch() {
  status("正在以 XP 兼容层启动真 IE6…", "");
  post("/api/ie6/compat-launch", { url: location.href }, function (d) {
    status(d.ok ? "完成" : "无法在 Win11 上直接运行", d.message || "");
    setText("ie6-note", esc(d.message || ""));
  });
}

function launchWslIe6() {
  post("/api/ie6/wsl/launch", { url: location.href }, function (d) { status(d.ok ? "完成" : "失败", d.message || ""); });
}

function showWslCommands() {
  api("/api/ie6/wsl", function (d) {
    var text = "WSL 准备命令（在管理员 PowerShell 里执行）：\n\n" + (d.prepareCommands || []).join("\n");
    setText("ie6-note", esc(text).replace(/\n/g, "<br>"));
    status("已显示 WSL 准备命令", "");
  });
}

function loadVmIe6() {
  api("/api/vmie6", function (d) {
    var vms = d.vms || [];
    if (vms.length > 0) $("vm-path").value = d.configuredVm || vms[0].path;
    if (d.guestUser) $("vm-user").value = d.guestUser;
    setText("vm-status", "vmrun " + (d.vmrunFound ? "可用" : "不可用") + "；虚拟机 " + vms.length +
      " 台，运行中 " + (d.running || 0) + " 台" + (d.hasPassword ? "；已保存密码" : "；未填密码"));
  });
}

function saveVmSettings(done) {
  post("/api/settings", {
    vmPath: $("vm-path").value,
    vmGuestUser: $("vm-user").value,
    vmGuestPassword: $("vm-pass").value,
    vmIe6Path: $("vm-ie").value
  }, function (d) { if (done) done(d); });
}

function launchVmIe6() {
  status("正在让 XP 客户机里的原版 IE6 打开本页…", "");
  saveVmSettings(function () {
    post("/api/vmie6/launch", { url: location.href }, function (d) {
      status(d.ok ? "原版 IE6 已打开" : "失败", d.message || "");
      setText("vm-note", esc(d.message || ""));
      if (d.ok) $("vm-image").src = "/vm-screen.png?t=" + new Date().getTime();
    });
  });
}

function captureVmScreen() {
  status("正在抓取客户机屏幕…", "");
  saveVmSettings(function () {
    post("/api/vmie6/screen", {}, function (d) {
      status(d.ok ? "完成" : "失败", d.message || "");
      setText("vm-note", esc(d.message || ""));
      $("vm-image").src = "/vm-screen.png?t=" + new Date().getTime();
    });
  });
}

/* ------------------------------------------------------- 启动 */

function boot() {
  refreshAll();
  pollLogs();
  setInterval(pollLogs, 2000);
  pollGameConsole();
  setInterval(pollGameConsole, 1500);
  setInterval(function () { if (!document.hidden) refreshAll(); }, 8000);

  // 自证：脚本完整执行到了这里（否则启动器日志里只会看到脚本错误）
  var ping = new Image();
  ping.src = '/api/clientready?charset=' + encodeURIComponent(document.charset || 'unknown');
}

/* IE6 只有 attachEvent；现代浏览器走 addEventListener —— 这里优先 IE6 的写法 */
if (window.attachEvent) window.attachEvent('onload', boot);
else if (window.addEventListener) window.addEventListener('load', boot, false);
else window.onload = boot;
