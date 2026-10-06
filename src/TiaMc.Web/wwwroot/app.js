/* =========================================================================
   TiaMC-Web 前端逻辑（无框架，仿 IE6 风格界面）
   与内核通过 /api/* 通信；日志轮询；Flash 用 Ruffle 或投影播放器。
   ========================================================================= */

const $ = (id) => document.getElementById(id);
const state = {
  logCursor: 0,
  logLevel: '全部',
  instance: '',
  remote: [],
  flash: null,
  settings: null
};

/* ---------------------------------------------------------------- 工具 */

async function api(path, options) {
  const response = await fetch(path, Object.assign({ headers: { 'Content-Type': 'application/json' } }, options || {}));
  const text = await response.text();
  try {
    return JSON.parse(text);
  } catch (e) {
    return { ok: false, message: text };
  }
}

async function post(path, body) {
  return api(path, { method: 'POST', body: JSON.stringify(body || {}) });
}

function status(text, detail) {
  $('status-main').textContent = text;
  if (detail !== undefined) $('status-detail').textContent = detail;
}

function busy(text) {
  status('正在打开网页…', text || 'http://127.0.0.1/');
}

function esc(value) {
  return String(value === undefined || value === null ? '' : value)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

/* ---------------------------------------------------------------- 页签 */

document.querySelectorAll('#ie-tabs .ie-tab').forEach((tab) => {
  tab.addEventListener('click', () => {
    document.querySelectorAll('#ie-tabs .ie-tab').forEach((t) => t.classList.remove('active'));
    document.querySelectorAll('.page').forEach((p) => p.classList.remove('active'));
    tab.classList.add('active');
    $('page-' + tab.dataset.tab).classList.add('active');
    location.hash = tab.dataset.tab;
    status('完成', 'http://127.0.0.1/#' + tab.dataset.tab);
    const loader = loaders[tab.dataset.tab];
    if (loader) loader();
  });
});

const loaders = {
  overview: loadState,
  versions: loadVersions,
  accounts: loadAccounts,
  mods: loadMods,
  settings: loadSettings,
  logs: loadLogs,
  console: loadGameConsole,
  flash: loadFlash
};

/* --------------------------------------------------- MC 控制台（客户端输出） */

let consoleTimer = null;
let consoleCursor = 0;

async function loadGameConsole() {
  await pollGameConsole(0);
  if (!consoleTimer) consoleTimer = setInterval(() => pollGameConsole(consoleCursor), 1000);
}

async function pollGameConsole(since) {
  const data = await api('/api/gameconsole?since=' + since);
  const box = $('game-box');
  if (since === 0) box.innerHTML = '';
  consoleCursor = data.next || 0;

  (data.lines || []).forEach((line) => {
    const span = document.createElement('span');
    let cls = 'game';
    if (/\[.*(ERROR|SEVERE)\/\]/.test(line) || line.includes('Exception')) cls = 'err';
    else if (line.includes('WARN')) cls = 'warn';
    span.className = cls;
    span.textContent = line + '\n';
    box.appendChild(span);
  });

  $('gc-state').textContent = data.running ? '运行中' : (data.exitCode === undefined || data.exitCode === null ? '未启动' : '已退出');
  $('gc-exit').textContent = data.running
    ? ('PID ' + (data.pid ?? ''))
    : (data.exitCode === undefined || data.exitCode === null ? '—'
      : ('退出码 ' + data.exitCode + (data.exitCode === 0 ? '（正常）' : ' ← 有问题，点「问题诊断」')));
  $('gc-java').textContent = data.java || '—';
  $('gc-natives').textContent = data.natives || '—';
  $('gc-cp').textContent = data.classpathCount || 0;
  $('console-status').textContent = data.running ? '客户端运行中' : (data.state || '');
  if ($('console-follow').checked) box.scrollTop = box.scrollHeight;

  // 异常退出（非 0）时自动给出诊断结论
  if (!data.running && data.exitCode !== undefined && data.exitCode !== null && data.exitCode !== 0) {
    await showGameDiagnosis();
  }
}

async function showGameDiagnosis() {
  try {
    const data = await api('/api/diagnose');
    let text = '严重程度: ' + (data.severity || '未知') + '\n' + (data.summary || '') + '\n';
    if (data.hints && data.hints.length) {
      text += '\n可能的处理建议:\n';
      data.hints.forEach((h) => { text += '  · ' + h + '\n'; });
    }
    if (data.keywords && data.keywords.length) {
      text += '\n命中的关键字: ' + data.keywords.join(', ') + '\n';
    }
    if (data.suspects && data.suspects.length) {
      text += '\n可疑模组: ' + data.suspects.join(', ') + '\n';
    }
    const plan = await api('/api/gameconsole?since=0');
    if (plan.summary) text += '\n启动方案: ' + plan.summary + '\n';
    if (plan.command) text += '\n命令行:\n' + plan.command + '\n';
    $('game-diag').textContent = text;
  } catch (e) {
    $('game-diag').textContent = '诊断失败: ' + e.message;
  }
}

/* ---------------------------------------------------------------- 概览 */

async function loadState() {
  busy('正在获取启动器状态…');
  const data = await api('/api/state');
  $('ov-instances').textContent = data.instances ?? 0;
  $('ov-active').textContent = '当前：' + (data.activeInstance || '未选择');
  $('ov-accounts').textContent = data.accounts ?? 0;
  $('ov-account').textContent = '当前：' + (data.selectedAccount || '无');
  $('ov-java').textContent = data.java ?? 0;
  $('ov-remote').textContent = data.remoteVersions ?? 0;
  $('ov-flash').textContent = data.flash && data.flash.ruffleReady ? 'Ruffle 就绪' : '未安装';
  $('ov-flash-note').textContent = data.flash && data.flash.projectorFound
    ? '已检测到投影播放器'
    : 'Ruffle / 投影播放器';
  $('root-path').textContent = data.root || '';
  state.instance = data.activeInstance || state.instance;
  document.title = 'TIA-MC 启动器（Web GUI 版） - ' + (data.activeInstance || '未选择实例');
  status('完成', data.root || '');
}

/* ---------------------------------------------------------------- 版本 */

async function loadVersions() {
  busy('正在读取版本清单…');
  const data = await api('/api/versions');

  const installed = $('installed-table').querySelector('tbody');
  installed.innerHTML = '';
  (data.installed || []).forEach((v) => {
    const tr = document.createElement('tr');
    tr.innerHTML = `<td>${esc(v.id)}</td><td>${esc(v.loader)}</td><td>Java ${esc(v.javaMajor)}</td>
      <td><button class="xp" data-launch="${esc(v.id)}">启动</button></td>`;
    installed.appendChild(tr);
  });
  if (!data.installed || !data.installed.length) {
    installed.innerHTML = '<tr><td colspan="4" class="gray">还没有实例：先在右侧安装一个正式版。</td></tr>';
  }

  state.remote = data.remote || [];
  renderRemote();
  status('完成', (data.installed || []).length + ' 个实例 / ' + (data.remoteTotal || 0) + ' 个在线版本');
}

function renderRemote() {
  const filter = $('remote-filter').value;
  const search = $('remote-search').value.trim().toLowerCase();
  const body = $('remote-table').querySelector('tbody');
  body.innerHTML = '';

  const rows = state.remote.filter((v) => {
    const type = (v.type || '').toLowerCase();
    if (filter === 'release' && type !== 'release') return false;
    if (filter === 'snapshot' && type !== 'snapshot') return false;
    if (filter === 'old' && !type.startsWith('old_')) return false;
    if (search && !(v.id || '').toLowerCase().includes(search)) return false;
    return true;
  });

  rows.slice(0, 600).forEach((v) => {
    const type = (v.type || '').toLowerCase();
    const label = type === 'release' ? '正式版' : type === 'snapshot' ? '快照（测试版）'
      : type === 'old_beta' ? '旧版 Beta' : type === 'old_alpha' ? '旧版 Alpha' : v.type;
    const tr = document.createElement('tr');
    if (type === 'snapshot' || type.startsWith('old_')) tr.className = 'snapshot';
    tr.innerHTML = `<td>${esc(v.id)}</td><td>${esc(label)}</td><td>${esc((v.releaseTime || '').substring(0, 10))}</td>
      <td><button class="xp" data-install="${esc(v.id)}">安装</button></td>`;
    body.appendChild(tr);
  });

  $('remote-count').textContent = `（显示 ${Math.min(rows.length, 600)} / 共 ${rows.length} 个）`;
  status('完成', rows.length + ' 个版本');
}

$('remote-filter').addEventListener('change', renderRemote);
$('remote-search').addEventListener('input', renderRemote);

/* ---------------------------------------------------------------- 账户 */

async function loadAccounts() {
  const data = await api('/api/accounts');
  const body = $('accounts-table').querySelector('tbody');
  body.innerHTML = '';
  (data.accounts || []).forEach((a) => {
    const tr = document.createElement('tr');
    if (a.selected) tr.className = 'selected';
    tr.innerHTML = `<td>${esc(a.name)}${a.selected ? ' <span class="gray">(当前)</span>' : ''}</td>
      <td>${esc(a.kind)}</td><td class="mono">${esc(a.uuid)}</td><td>${esc(a.server || '—')}</td>
      <td>${a.selected ? '' : `<button class="xp" data-account="${esc(a.key)}">切换</button>`}</td>`;
    body.appendChild(tr);
  });
  status('完成', (data.accounts || []).length + ' 个账户');
}

/* ---------------------------------------------------------------- 模组 */

async function loadMods() {
  const data = await api('/api/mods?instance=' + encodeURIComponent(state.instance));
  $('mods-dir').textContent = data.directory || '';
  const body = $('mods-table').querySelector('tbody');
  body.innerHTML = '';
  (data.mods || []).forEach((m) => {
    const tr = document.createElement('tr');
    tr.innerHTML = `<td>${esc(m.name)}</td><td>${esc(m.version)}</td><td>${esc(m.loader)}</td>
      <td class="mono">${esc(m.file)}</td><td>${esc(m.size)}</td>
      <td>${m.enabled ? '已启用' : '已停用'}</td>
      <td><button class="xp" data-mod="${esc(m.file)}">${m.enabled ? '停用' : '启用'}</button></td>`;
    body.appendChild(tr);
  });
  if (!data.mods || !data.mods.length) body.innerHTML = '<tr><td colspan="7" class="gray">该实例没有模组。</td></tr>';
  status('完成', (data.count || 0) + ' 个模组');
}

/* ---------------------------------------------------------------- 设置 */

async function loadSettings() {
  const data = await api('/api/settings');
  state.settings = data;
  $('set-memory').value = data.maxMemoryMb;
  $('set-memory-hint').textContent =
    `本机物理内存 ${Math.round((data.physicalMemoryMb || 0) / 1024)} GB，建议 ${Math.round((data.recommendedMemoryMb || 0) / 1024)} GB（只设 -Xmx）`;
  $('set-jvm').value = data.extraJvmArgs || '';
  $('set-java').value = data.javaPath || '';
  $('set-projector').value = data.flashProjectorPath || '';
  $('set-root').value = data.minecraftRoot || '';

  const gc = $('set-gc');
  gc.innerHTML = '';
  (data.gcModes || []).forEach((mode) => {
    const option = document.createElement('option');
    option.value = mode;
    option.textContent = mode;
    if (mode === data.gcMode) option.selected = true;
    gc.appendChild(option);
  });
  $('settings-hint').textContent = '配置目录与日志可在桌面版查看；Web 版修改后立即写入 config.json。';
  status('完成', '设置已载入');
}

/* ---------------------------------------------------------------- 日志 */

let logTimer = null;

async function loadLogs() {
  await pollLogs(true);
  // 日志页打开后每 1.5 秒拉一次增量：客户端输出（[GAME]）能实时滚出来
  if (!logTimer) logTimer = setInterval(() => pollLogs(false), 1500);
}

async function pollLogs(force) {
  const data = await api('/api/logs?since=' + (force ? 0 : state.logCursor));
  if (force) $('log-box').innerHTML = '';
  state.logCursor = data.next || 0;
  const box = $('log-box');
  (data.lines || []).forEach((line) => {
    if (state.logLevel !== '全部' && !line.includes('[' + state.logLevel + ']')) return;
    const span = document.createElement('span');
    let cls = '';
    if (line.includes('[WARN]')) cls = 'warn';
    else if (line.includes('[ERR]')) cls = 'err';
    else if (line.includes('[动作]')) cls = 'action';
    else if (line.includes('[OK]')) cls = 'ok';
    else if (line.includes('[GAME]')) cls = 'game';
    span.className = cls;
    span.textContent = line + '\n';
    box.appendChild(span);
  });
  $('log-count').textContent = `共 ${data.total || 0} 条，已显示 ${state.logCursor}`;
  if ($('log-follow').checked) box.scrollTop = box.scrollHeight;
}

$('log-level').addEventListener('change', () => {
  state.logLevel = $('log-level').value;
  pollLogs(true);
});

/* ---------------------------------------------------------------- Flash */

async function loadFlash() {
  const data = await api('/api/flash');
  state.flash = data;
  $('flash-note').textContent = data.note || '';
  $('flash-ruffle').textContent = data.ruffleReady
    ? `已就绪（${data.ruffleVersion || 'nightly'}）`
    : '未下载：点下面的按钮获取（约 10 MB，来自 GitHub Ruffle 官方发布）';
  $('flash-projector').textContent = data.projectorFound
    ? data.projector
    : '未找到 flashplayer_*.exe：可在「设置」里指定路径';
  $('flash-count').textContent = (data.swfs || []).length + ' 个 SWF';
  $('flash-dir').textContent = data.swfDirectory || '';
  $('flash-save-dir').textContent = data.swfDirectory || '';

  const body = $('flash-table').querySelector('tbody');
  body.innerHTML = '';
  (data.swfs || []).forEach((swf) => {
    const tr = document.createElement('tr');
    tr.innerHTML = `<td class="mono">${esc(swf.name)}</td><td>${esc(swf.size)}</td><td>${esc(swf.modified)}</td>
      <td><button class="xp" data-swf="${esc(swf.name)}">播放</button>
          <button class="xp" data-swf-projector="${esc(swf.name)}">投影</button></td>`;
    body.appendChild(tr);
  });
  if (!data.swfs || !data.swfs.length) {
    body.innerHTML = '<tr><td colspan="4" class="gray">SWF 库是空的：拖入 .swf 或点「浏览…」导入。</td></tr>';
  }
  status('完成', data.ruffleReady ? 'Ruffle 就绪' : 'Ruffle 未安装');
}

/** 用 Ruffle 在页面内播放（<object> 会被 Ruffle polyfill 接管） */
function playSwf(url) {
  const stage = $('flash-stage');
  stage.innerHTML =
    '<object type="application/x-shockwave-flash" data="' + esc(url) + '" width="100%" height="360">' +
    '<param name="movie" value="' + esc(url) + '" />' +
    '<param name="quality" value="high" />' +
    '<param name="allowScriptAccess" value="sameDomain" />' +
    '<embed src="' + esc(url) + '" type="application/x-shockwave-flash" width="100%" height="360" quality="high" />' +
    '</object>';

  // Ruffle 已加载时，polyfill 会自动替换上面的 object；再提示一次状态
  const hasRuffle = typeof window.RufflePlayer !== 'undefined' || document.querySelector('ruffle-player');
  status(hasRuffle ? 'Flash 内容正在用 Ruffle 播放' : '已提交给浏览器（未检测到 Ruffle，请先获取 Ruffle）', url);
}

/** 用 iframe 打开普通网页（例如 Flash 页游站点） */
function openPage(url) {
  $('flash-stage').innerHTML = '<iframe src="' + esc(url) + '" referrerpolicy="no-referrer"></iframe>';
  status('正在打开网页…', url);
}

/* ---------------------------------------------------------------- 事件 */

document.addEventListener('click', async (event) => {
  const target = event.target.closest('button');
  if (!target) return;
  const act = target.dataset.act;
  const launchId = target.dataset.launch;
  const installId = target.dataset.install;
  const accountKey = target.dataset.account;
  const modFile = target.dataset.mod;
  const swf = target.dataset.swf;
  const swfProjector = target.dataset.swfProjector;

  if (launchId) {
    busy('正在启动 ' + launchId + '…');
    const result = await post('/api/launch', { instance: launchId });
    $('plan-box').textContent = (result.message || '') + '\n\nJava: ' + (result.java || '') +
      '\n\n' + (result.command || '');
    status(result.ok ? '启动成功' : '启动失败', result.message || '');
    return;
  }

  if (installId) {
    busy('正在安装 ' + installId + '…（可能需要几分钟）');
    const result = await post('/api/instance/install', { id: installId });
    status(result.ok ? '安装完成' : '安装失败', result.message || '');
    await loadVersions();
    return;
  }

  if (accountKey) {
    const result = await post('/api/accounts/select', { key: accountKey });
    status(result.ok ? '完成' : '失败', result.message || '');
    await loadAccounts();
    await loadState();
    return;
  }

  if (modFile) {
    const result = await post('/api/mods/toggle', { instance: state.instance, file: modFile });
    status(result.ok ? '完成' : '失败', result.message || '');
    await loadMods();
    return;
  }

  if (swf) {
    playSwf('/flash/swf/' + encodeURIComponent(swf));
    return;
  }

  if (swfProjector) {
    const result = await post('/api/flash/open', { target: swfProjector });
    status(result.ok ? '完成' : '失败', result.message || '');
    return;
  }

  switch (act) {
    case 'refresh':
      await loadState();
      break;

    case 'manifest': {
      busy('正在从 BMCLAPI / 官方源获取版本清单…');
      const result = await post('/api/manifest/refresh');
      status(result.ok ? '完成' : '失败', '在线版本 ' + (result.versions || 0) + ' 个');
      await loadVersions();
      break;
    }

    case 'shutdown': {
      busy('正在结束游戏并关闭界面…');
      await post('/api/stop', {});
      const bye = await post('/api/shutdown', {});
      status('已关闭', bye.message || '');
      document.body.innerHTML = '<div style="padding:24px;font-size:14px">界面已关闭，游戏也已结束。可以直接关掉这个窗口。</div>';
      break;
    }
    case 'repair': {
      busy('正在校验并补齐文件…');
      const fixed = await post('/api/instance/repair', { id: state.instance });
      status(fixed.ok ? '补齐完成' : '补齐未完成', fixed.message || '');
      await loadState();
      break;
    }
    case 'check': {
      busy('正在校验文件…');
      const result = await post('/api/launch', { instance: state.instance, checkOnly: true });
      status('校验完成', result.message || '');
      break;
    }

    case 'clear-console': {
      consoleCursor = 0;
      $('game-box').innerHTML = '';
      $('game-diag').textContent = '还没有诊断结果。';
      status('控制台视图已清空', '');
      break;
    }
    case 'game-diagnose': {
      busy('正在分析启动日志与崩溃报告…');
      await showGameDiagnosis();
      status('诊断完成', '结论见「启动方案 / 问题诊断」');
      break;
    }
    case 'launch': {
      busy('正在启动游戏…');
      const result = await post('/api/launch', { instance: state.instance });
      $('plan-box').textContent = (result.message || '') + '\n\nJava: ' + (result.java || '') +
        '\n\n' + (result.command || '');
      status(result.ok ? '游戏已启动' : '启动失败', result.message || '');
      break;
    }

    case 'stop': {
      const result = await post('/api/stop');
      status('完成', result.message || '');
      break;
    }

    case 'diagnose': {
      busy('正在分析日志与崩溃报告…');
      const result = await post('/api/diagnose');
      $('diag-box').textContent = '严重程度: ' + (result.severity || '') + '\n' + (result.summary || '') +
        '\n\n建议:\n- ' + ((result.hints || []).join('\n- ')) +
        '\n\n疑似模组: ' + ((result.suspects || []).join(', ') || '无') +
        '\n关键字: ' + ((result.keywords || []).join(', ') || '无');
      status('完成', '诊断结束');
      break;
    }

    case 'save-settings': {
      const result = await post('/api/settings', {
        instance: state.instance,
        maxMemoryMb: parseInt($('set-memory').value, 10) || 0,
        gcMode: $('set-gc').value,
        extraJvmArgs: $('set-jvm').value,
        javaPath: $('set-java').value,
        flashProjectorPath: $('set-projector').value,
        minecraftRoot: $('set-root').value
      });
      status(result.ok ? '完成' : '失败', result.message || '');
      break;
    }

    case 'java-provision': {
      busy('正在下载 Java 运行库…');
      const result = await post('/api/java/provision', { major: 17 });
      status(result.ok ? '完成' : '失败', result.message || '');
      await loadState();
      break;
    }

    case 'mods-refresh':
      await loadMods();
      break;

    case 'clear-log-view':
      $('log-box').innerHTML = '';
      break;

    case 'flash-ruffle': {
      busy('正在从 GitHub 下载 Ruffle…');
      const result = await post('/api/flash/ruffle');
      status(result.ok ? '完成' : '失败', result.message || '');
      await loadFlash();
      // ruffle.js 是页面加载时引入的，下载完成后重载一次生效
      if (result.ok && !document.querySelector('ruffle-player')) {
        $('flash-stage').innerHTML = '<p class="gray">Ruffle 已下载，正在重新载入页面以启用…</p>';
        setTimeout(() => location.reload(), 1200);
      }
      break;
    }

    case 'flash-projector-open': {
      const result = await post('/api/flash/projector');
      status(result.ok ? '完成' : '失败', result.message || '');
      break;
    }
  }
});

/* 账户新建 */
$('account-add').addEventListener('click', async () => {
  const name = $('account-name').value.trim() || 'Steve';
  const result = await post('/api/accounts/offline', { name });
  status(result.ok ? '完成' : '失败', result.message || '');
  await loadAccounts();
  await loadState();
});

/* Flash 播放 / 投影 */
$('flash-play').addEventListener('click', async () => {
  const value = $('flash-url').value.trim();
  if (!value) return;
  if (/\.swf(\?|$)/i.test(value)) {
    playSwf(value);
  } else {
    openPage(value);
  }
});

$('flash-projector').addEventListener('click', async () => {
  const value = $('flash-url').value.trim();
  const result = await post('/api/flash/open', { target: value });
  status(result.ok ? '完成' : '失败', result.message || '');
});

/* 本地 SWF：读成 base64 上传到 SWF 库，然后播放 */
async function uploadSwf(file) {
  busy('正在导入 ' + file.name + '…');
  const buffer = await file.arrayBuffer();
  let binary = '';
  const bytes = new Uint8Array(buffer);
  for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
  const data = btoa(binary);
  const result = await post('/api/flash/upload', { name: file.name, data: data });
  status(result.ok ? '完成' : '失败', result.message || '');
  if (result.ok) await loadFlash();
  return result;
}

$('flash-file').addEventListener('change', async (event) => {
  const files = Array.from(event.target.files || []);
  for (const file of files) {
    const result = await uploadSwf(file);
    if (result.ok) playSwf('/flash/swf/' + encodeURIComponent(result.file));
  }
  event.target.value = '';
});

/* 拖放到舞台 */
const stage = $('flash-stage');
stage.addEventListener('dragover', (event) => { event.preventDefault(); stage.classList.add('drag'); });
stage.addEventListener('dragleave', () => stage.classList.remove('drag'));
stage.addEventListener('drop', async (event) => {
  event.preventDefault();
  stage.classList.remove('drag');
  const files = Array.from((event.dataTransfer && event.dataTransfer.files) || []);
  for (const file of files) {
    const result = await uploadSwf(file);
    if (result.ok) playSwf('/flash/swf/' + encodeURIComponent(result.file));
  }
});

/* 地址栏显示真实 URL */
$('addr').textContent = location.origin + '/';

/* 轮询日志（1.5 秒）与状态（5 秒） */
setInterval(() => {
  if ($('page-logs').classList.contains('active')) pollLogs(false);
}, 1500);
setInterval(() => {
  if ($('page-overview').classList.contains('active')) loadState();
}, 5000);

/* 初始加载 */
const initial = (location.hash || '#overview').replace('#', '');
const initialTab = document.querySelector(`.ie-tab[data-tab="${initial}"]`);
if (initialTab) initialTab.click();
else loadState();
