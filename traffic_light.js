// Semaforo do Claude Code.
//   node traffic_light.js       -> painel no terminal; recebe os eventos dos hooks HTTP em localhost:4545
//   node traffic_light.js --ptbr -> idem, com textos em pt-br (padrao: ingles)
//   node traffic_light.js --app -> painel so na janela (Edge em modo app). Fechar a janela encerra o semaforo.
//   traffic_light.exe [--ptbr]   -> versao autossuficiente (traffic_light.cs): bandeja + widget, sem node. Nao rode junto com este (mesma porta).
// Ao abrir, confere os hooks e avisa se faltar algum (igual ao exe).
//   node traffic_light.js --install -> adiciona os hooks em ~/.claude/settings.json (com backup) e roda o --doctor
//   node traffic_light.js --doctor  -> confere hooks, Edge e se o semaforo esta rodando
// Estado so em memoria: abra o painel antes das sessoes (ou espere o proximo evento de cada uma).
const fs = require('fs'), path = require('path'), http = require('http'), { exec } = require('child_process');
const PORT = 4545;
const STATE = {
  SessionStart: 'idle',
  UserPromptSubmit: 'busy',
  PostToolUse: 'busy', // volta pra vermelho depois de aprovar uma permissao / responder pergunta
  PreToolUse: 'waiting', // so registrado para AskUserQuestion: a pergunta abre na hora
  PermissionRequest: 'waiting',
  Notification: 'waiting',
  Stop: 'idle',
};
const PTBR = process.argv.includes('--ptbr');
const T = PTBR
  ? { waiting: ['AGUARDANDO VOCE', 'aguardando'], busy: ['ATIVO', 'ativos'], idle: ['LIVRE', 'livres'], none: 'nenhuma sessao', locale: 'pt-BR', title: 'Instâncias de agentes de IA', app: 'painel aberto na janela; fechar a janela encerra o semaforo' }
  : { waiting: ['WAITING FOR YOU', 'waiting'], busy: ['BUSY', 'busy'], idle: ['IDLE', 'idle'], none: 'no sessions', locale: 'en-US', title: 'AI Agent instances', app: 'panel opened in the window; closing the window stops the traffic light' };
// ordem = prioridade na lista: quem precisa de voce aparece primeiro
const LOOK = {
  waiting: { color: 33, label: T.waiting[0], plural: T.waiting[1], order: 0 },
  busy: { color: 31, label: T.busy[0], plural: T.busy[1], order: 1 },
  idle: { color: 32, label: T.idle[0], plural: T.idle[1], order: 2 },
};
const c = (code, s) => `\x1b[${code}m${s}\x1b[0m`;
const sessions = new Map(); // session_id -> { state, cwd, at, watcher, offset }

const set = (s, state) => { s.state = state; s.at = Date.now(); render(); };

// Esc nao dispara hook nenhum, mas o Claude Code grava a interrupcao no transcript na hora.
// Le so os bytes novos desde a ultima leitura (o arquivo so cresce).
function watchTranscript(s, file) {
  try { s.offset = fs.statSync(file).size; } catch { return; }
  s.watcher = fs.watch(file, () => {
    let chunk;
    try {
      const size = fs.statSync(file).size;
      if (size <= s.offset) return;
      const fd = fs.openSync(file, 'r'), buf = Buffer.alloc(size - s.offset);
      fs.readSync(fd, buf, 0, buf.length, s.offset);
      fs.closeSync(fd);
      chunk = buf.toString('utf8');
      s.offset += Buffer.byteLength(chunk.slice(0, chunk.lastIndexOf('\n') + 1)); // linha pela metade fica pra proxima
    } catch { return; }
    for (const line of chunk.split('\n')) {
      if (!line.includes('[Request interrupted by user')) continue; // filtro barato antes do parse
      try {
        const m = JSON.parse(line);
        const content = m.message?.content;
        const text = typeof content === 'string' ? content : content?.[0]?.text;
        if (m.type === 'user' && text?.startsWith('[Request interrupted by user')) return set(s, 'idle');
      } catch {}
    }
  });
}

function onEvent(e) {
  let s = sessions.get(e.session_id);
  if (e.hook_event_name === 'SessionEnd') {
    s?.watcher?.close();
    sessions.delete(e.session_id);
    return render();
  }
  // idle_prompt = ociosa ha ~60s; rede de seguranca caso a deteccao pelo transcript falhe
  const state = e.notification_type === 'idle_prompt' ? 'idle' : STATE[e.hook_event_name];
  if (!state) return;
  if (!s) {
    s = {};
    sessions.set(e.session_id, s);
    if (e.transcript_path) watchTranscript(s, e.transcript_path);
  }
  s.cwd = e.cwd || s.cwd || '?';
  set(s, state);
}

// mesmo conteudo para o terminal e para a janela
const folder = s => path.basename(s.cwd.replace(/[\\/]+$/, ''));
function view() {
  const rows = [...sessions.values()].sort((a, b) => LOOK[a.state].order - LOOK[b.state].order || folder(a).localeCompare(folder(b), undefined, { sensitivity: 'base' }));
  return {
    summary: Object.entries(LOOK).map(([k, l]) => ({ state: k, text: `${rows.filter(r => r.state === k).length} ${l.plural}` })),
    rows: rows.map(r => ({ state: r.state, label: LOOK[r.state].label, name: folder(r), at: new Date(r.at).toLocaleTimeString(T.locale) })),
    none: T.none,
    warn,
  };
}

const clients = new Set(); // janelas abertas (SSE)
const APP = process.argv.includes('--app');
const openWindow = () => exec(`start "" msedge --app=http://localhost:${PORT}/ --window-size=560,320`);
function render() {
  const v = view();
  for (const res of clients) res.write(`data: ${JSON.stringify(v)}\n\n`);
  if (APP) return;
  const summary = v.summary.map(s => c(LOOK[s.state].color, s.text)).join(c(90, ' · '));
  const lines = v.rows.map(r => ` ${c(LOOK[r.state].color, '● ' + r.label.padEnd(16))}${r.name.padEnd(28)}${c(90, r.at)}`);
  process.stdout.write('\x1b[2J\x1b[H ' + summary + '\n\n' + (lines.join('\n') || c(90, ' ' + T.none)) + '\n' + (warn && '\n' + c(33, ' ' + warn) + '\n'));
}

const PAGE = `<!doctype html><meta charset="utf-8"><title>${T.title}</title>
<style>
body{margin:0;padding:12px 16px;background:#0c0c0c;color:#ccc;font:14px Consolas,monospace}
.waiting{color:#f9f1a5}.busy{color:#e74856}.idle{color:#16c60c}.dim{color:#767676}
.row{display:grid;grid-template-columns:12em 1fr auto;gap:8px;padding:2px 0}
</style>
<div id="sum"></div><br><div id="rows"></div><br><div id="warn" class="waiting"></div>
<script>
const el = (cls, text) => Object.assign(document.createElement('span'), { className: cls, textContent: text });
new EventSource('/events').onmessage = e => {
  const v = JSON.parse(e.data);
  sum.replaceChildren(...v.summary.flatMap((s, i) => [i ? el('dim', ' · ') : '', el(s.state, s.text)]));
  rows.replaceChildren(...(v.rows.length ? v.rows.map(r => {
    const d = document.createElement('div');
    d.className = 'row';
    d.append(el(r.state, '● ' + r.label), el('', r.name), el('dim', r.at));
    return d;
  }) : [el('dim', v.none)]));
  warn.textContent = v.warn;
};
</script>`;

// --install / --doctor: configura e confere os hooks em ~/.claude/settings.json
const L = (en, pt) => (PTBR ? pt : en);
const SETTINGS = path.join(require('os').homedir(), '.claude', 'settings.json');
const HOOK_URL = `http://127.0.0.1:${PORT}`;
const EVENTS = [...Object.keys(STATE), 'SessionEnd'];
const MATCHERS = { PreToolUse: 'AskUserQuestion', Notification: 'permission_prompt|elicitation_dialog|idle_prompt' };
const isOurs = b => b.hooks?.some(h => h.type === 'http' && new RegExp(`^http://(127\\.0\\.0\\.1|localhost):${PORT}/?$`).test(h.url));

function readSettings() {
  if (!fs.existsSync(SETTINGS)) return {};
  const txt = fs.readFileSync(SETTINGS, 'utf8');
  return txt.trim() ? JSON.parse(txt) : {};
}

// mesma checagem do exe: lista do que esta errado nos hooks
function hookProblems(cfg) {
  const out = [];
  if (cfg.disableAllHooks) out.push(L('"disableAllHooks": true turns off every hook', '"disableAllHooks": true desliga todos os hooks'));
  for (const ev of EVENTS) {
    const block = cfg.hooks?.[ev]?.find(isOurs);
    if (!block) out.push(L(`${ev}: hook missing`, `${ev}: hook faltando`));
    else if ((block.matcher || '') !== (MATCHERS[ev] || '')) out.push(L(`${ev}: matcher should be "${MATCHERS[ev] || ''}", found "${block.matcher || ''}"`, `${ev}: matcher deveria ser "${MATCHERS[ev] || ''}", esta "${block.matcher || ''}"`));
  }
  return out;
}

function install() {
  let cfg;
  try { cfg = readSettings(); } catch (e) {
    console.error(L(`[!!] ${SETTINGS} is not valid JSON, fix it first: ${e.message}`, `[!!] ${SETTINGS} nao e um JSON valido, corrija antes: ${e.message}`));
    process.exit(1);
  }
  cfg.hooks ??= {};
  const added = EVENTS.filter(ev => !(cfg.hooks[ev] ??= []).some(isOurs));
  for (const ev of added) cfg.hooks[ev].push({ ...(MATCHERS[ev] && { matcher: MATCHERS[ev] }), hooks: [{ type: 'http', url: HOOK_URL, timeout: 2 }] });
  if (added.length) {
    fs.mkdirSync(path.dirname(SETTINGS), { recursive: true });
    if (fs.existsSync(SETTINGS)) fs.copyFileSync(SETTINGS, SETTINGS + '.bak'); // nunca perde a config do usuario
    fs.writeFileSync(SETTINGS, JSON.stringify(cfg, null, 2) + '\n');
    console.log(L(`[ok] hooks added: ${added.join(', ')} (backup: settings.json.bak)`, `[ok] hooks adicionados: ${added.join(', ')} (backup: settings.json.bak)`));
    console.log(L('     restart open Claude Code sessions to pick them up', '     reinicie as sessoes abertas do Claude Code para valerem'));
  } else console.log(L('[ok] hooks were already installed', '[ok] hooks ja estavam instalados'));
  doctor();
}

function doctor() {
  let problems = 0;
  const ok = msg => console.log('[ok] ' + msg), bad = msg => (problems++, console.log('[!!] ' + msg));
  let cfg;
  try { cfg = readSettings(); ok(SETTINGS); } catch (e) { bad(L(`invalid JSON in ${SETTINGS}: ${e.message}`, `JSON invalido em ${SETTINGS}: ${e.message}`)); cfg = {}; }
  const hp = hookProblems(cfg);
  hp.forEach(bad);
  if (!hp.length) ok('hooks: ' + EVENTS.join(', '));
  const edge = ['ProgramFiles(x86)', 'ProgramFiles'].some(v => process.env[v] && fs.existsSync(path.join(process.env[v], 'Microsoft', 'Edge', 'Application', 'msedge.exe')));
  edge ? ok('Microsoft Edge') : bad(L('Microsoft Edge not found (needed only for --app)', 'Microsoft Edge nao encontrado (so precisa para --app)'));
  http.get(HOOK_URL, { timeout: 1000 }, res => {
    let body = '';
    res.on('data', d => (body += d)).on('end', () => {
      /EventSource|^traffic_light$/.test(body) ? ok(L(`running on port ${PORT}`, `rodando na porta ${PORT}`)) : bad(L(`port ${PORT} is used by another program`, `porta ${PORT} esta em uso por outro programa`));
      finish();
    });
  }).on('error', () => { console.log(L(`[--] not running right now (port ${PORT} free)`, `[--] nao esta rodando agora (porta ${PORT} livre)`)); finish(); });
  function finish() {
    console.log(problems ? L(`\n${problems} problem(s) found (missing hooks: run --install)`, `\n${problems} problema(s) encontrado(s) (hooks faltando: rode --install)`) : L('\nall good', '\ntudo certo'));
    process.exitCode = problems ? 1 : 0;
  }
}

let warn = ''; // aviso de hooks, mostrado embaixo do painel
if (process.argv.includes('--install')) install();
else if (process.argv.includes('--doctor')) doctor();
else {
  try {
    const p = hookProblems(readSettings());
    if (p.length) warn = L('hooks need attention (run --install): ', 'hooks precisam de ajuste (rode --install): ') + p.join('; ');
  } catch (e) { warn = L(`invalid JSON in ${SETTINGS}: ${e.message}`, `JSON invalido em ${SETTINGS}: ${e.message}`); }
  serve();
}

function serve() {
http.createServer((req, res) => {
  if (req.method === 'GET') { // janela; os hooks sempre chegam por POST
    if (req.url !== '/events') return res.end(PAGE);
    res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' });
    clients.add(res);
    req.on('close', () => {
      clients.delete(res);
      // janela fechada = app fechado (espera um pouco: F5 reconecta)
      if (APP) setTimeout(() => clients.size || process.exit(), 5000);
    });
    return res.write(`data: ${JSON.stringify(view())}\n\n`);
  }
  let buf = '';
  req.on('data', d => (buf += d)).on('end', () => {
    res.end(); // responde antes de processar: nunca segura o Claude Code
    try { onEvent(JSON.parse(buf)); } catch {}
  });
}).on('error', e => {
  // ja tem um semaforo rodando: so abre outra janela pra ele
  if (e.code !== 'EADDRINUSE') throw e;
  if (APP) { openWindow(); setTimeout(process.exit, 1000); return; }
  console.error(L(`port ${PORT} is already in use: another traffic light (exe or traffic_light.js) is probably running.`, `a porta ${PORT} ja esta em uso: provavelmente outro semaforo (exe ou traffic_light.js) esta rodando.`));
  process.exit(1);
}).listen(PORT, '127.0.0.1', () => {
  render();
  if (APP) console.log(' ' + T.app + (warn && '\n ' + warn)), openWindow();
});
}
