// traffic_light.exe: semaforo do Claude Code autossuficiente (sem node). Icone na bandeja + widget flutuante.
//   traffic_light.exe [--ptbr]
// Recebe os hooks HTTP em 127.0.0.1:4545 (mesma logica do traffic_light.js), confere os hooks no
// ~/.claude/settings.json ao abrir e oferece instalar o que faltar.
// Gerar: C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /out:traffic_light.exe traffic_light.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

class Session {
  public string State, Cwd, Transcript;
  public long Offset;
  public DateTime At; // ultima mudanca de estado
}

class TrafficLight : Form {
  const int PORT = 4545;
  const int PAD = 8, ROW = 22, DOT = 12;
  static readonly Dictionary<string, string> STATE = new Dictionary<string, string> {
    { "SessionStart", "idle" },
    { "UserPromptSubmit", "busy" },
    { "PostToolUse", "busy" },     // volta pra vermelho depois de aprovar uma permissao / responder pergunta
    { "PreToolUse", "waiting" },   // so registrado para AskUserQuestion: a pergunta abre na hora
    { "PermissionRequest", "waiting" },
    { "Notification", "waiting" },
    { "Stop", "idle" },
  };
  static readonly string[] EVENTS = STATE.Keys.Concat(new[] { "SessionEnd" }).ToArray();
  static readonly Dictionary<string, string> MATCHERS = new Dictionary<string, string> {
    { "PreToolUse", "AskUserQuestion" }, { "Notification", "permission_prompt|elicitation_dialog|idle_prompt" },
  };
  static readonly string[] ORDER = { "waiting", "busy", "idle" }; // quem precisa de voce primeiro
  static readonly Dictionary<string, Color> COLORS = new Dictionary<string, Color> {
    { "waiting", Color.FromArgb(0xf9, 0xf1, 0xa5) }, { "busy", Color.FromArgb(0xe7, 0x48, 0x56) },
    { "idle", Color.FromArgb(0x16, 0xc6, 0x0c) }, { "none", Color.Gray },
  };
  static readonly string SETTINGS = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
  static readonly Regex OUR_URL = new Regex(@"^http://(127\.0\.0\.1|localhost):" + PORT + "/?$");

  [DllImport("user32.dll")] static extern bool ReleaseCapture();
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);

  readonly bool ptbr;
  readonly CultureInfo culture;
  readonly Dictionary<string, string[]> T; // estado -> [rotulo, plural]
  readonly NotifyIcon tray = new NotifyIcon();
  readonly Dictionary<string, Icon> icons = new Dictionary<string, Icon>();
  readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>(); // so mexida na thread da UI
  readonly TcpListener listener = new TcpListener(IPAddress.Loopback, PORT);
  List<Session> rows = new List<Session>();

  string L(string en, string pt) { return ptbr ? pt : en; }
  static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; } // PostToolUse pode ser grande
  static string Str(IDictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as string : null; }

  TrafficLight(bool ptbr) {
    this.ptbr = ptbr;
    culture = new CultureInfo(ptbr ? "pt-BR" : "en-US");
    T = ptbr
      ? new Dictionary<string, string[]> { { "waiting", new[] { "AGUARDANDO VOCE", "aguardando" } }, { "busy", new[] { "ATIVO", "ativos" } }, { "idle", new[] { "LIVRE", "livres" } } }
      : new Dictionary<string, string[]> { { "waiting", new[] { "WAITING FOR YOU", "waiting" } }, { "busy", new[] { "BUSY", "busy" } }, { "idle", new[] { "IDLE", "idle" } } };
    FormBorderStyle = FormBorderStyle.None;
    TopMost = true;
    ShowInTaskbar = false;
    DoubleBuffered = true;
    StartPosition = FormStartPosition.Manual;
    BackColor = Color.FromArgb(12, 12, 12);
    Font = new Font("Consolas", 10);
    Opacity = 0.92;

    foreach (var kv in COLORS) icons[kv.Key] = DotIcon(kv.Value);
    tray.Icon = icons["none"];
    tray.ContextMenuStrip = new ContextMenuStrip();
    tray.ContextMenuStrip.Items.Add(L("Show/hide widget", "Mostrar/ocultar widget"), null, (s, e) => Visible = !Visible);
    tray.ContextMenuStrip.Items.Add(L("Check installation", "Verificar instalação"), null, (s, e) => CheckInstall(false));
    tray.ContextMenuStrip.Items.Add(L("Exit", "Sair"), null, (s, e) => Close());
    tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Visible = !Visible; };

    // arrasta pelo widget inteiro (finge que clicou na barra de titulo)
    MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); } };
    MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) tray.ContextMenuStrip.Show(Cursor.Position); };
  }

  protected override void OnLoad(EventArgs e) {
    base.OnLoad(e);
    Refresh2();
    var area = Screen.PrimaryScreen.WorkingArea; // canto de baixo, a direita
    Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
    tray.Visible = true;
    new Thread(Serve) { IsBackground = true }.Start();
    // Esc nao dispara hook nenhum, mas o Claude Code grava a interrupcao no transcript na hora
    var timer = new System.Windows.Forms.Timer { Interval = 1000 }; // ponytail: polling de 1s, FileSystemWatcher se precisar ser instantaneo
    timer.Tick += (s, ev) => CheckTranscripts();
    timer.Start();
    BeginInvoke((Action)(() => CheckInstall(true)));
  }

  // ---------- servidor HTTP (so o minimo que os hooks usam) ----------

  void Serve() {
    while (true) {
      TcpClient c;
      try { c = listener.AcceptTcpClient(); } catch { return; }
      ThreadPool.QueueUserWorkItem(_ => HandleClient(c));
    }
  }

  void HandleClient(TcpClient client) {
    using (client) try {
      var s = client.GetStream();
      s.ReadTimeout = 5000;
      var buf = new MemoryStream();
      var chunk = new byte[16384];
      int headerEnd = -1, length = 0;
      while (true) {
        int n = s.Read(chunk, 0, chunk.Length);
        if (n <= 0) break;
        buf.Write(chunk, 0, n);
        var all = buf.GetBuffer();
        if (headerEnd < 0) {
          headerEnd = IndexOf(all, (int)buf.Length, "\r\n\r\n");
          if (headerEnd >= 0) {
            var m = Regex.Match(Encoding.ASCII.GetString(all, 0, headerEnd), @"content-length:\s*(\d+)", RegexOptions.IgnoreCase);
            length = m.Success ? int.Parse(m.Groups[1].Value) : 0;
          }
        }
        if (headerEnd >= 0 && buf.Length >= headerEnd + 4 + length) break;
      }
      if (headerEnd < 0) return;
      var data = buf.GetBuffer();
      bool post = Encoding.ASCII.GetString(data, 0, 4) == "POST";
      // corpo vazio no POST: texto na resposta de hook pode virar contexto pro Claude
      var reply = Encoding.ASCII.GetBytes(post
        ? "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
        : "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 13\r\nConnection: close\r\n\r\ntraffic_light");
      s.Write(reply, 0, reply.Length); // responde antes de processar: nunca segura o Claude Code
      if (!post) return;
      var ev = Json().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(data, headerEnd + 4, length));
      BeginInvoke((Action)(() => OnEvent(ev)));
    } catch { }
  }

  static int IndexOf(byte[] a, int len, string pattern) {
    var p = Encoding.ASCII.GetBytes(pattern);
    for (int i = 0; i + p.Length <= len; i++) {
      int j = 0;
      while (j < p.Length && a[i + j] == p[j]) j++;
      if (j == p.Length) return i;
    }
    return -1;
  }

  // ---------- estado ----------

  void OnEvent(Dictionary<string, object> e) {
    var id = Str(e, "session_id");
    var name = Str(e, "hook_event_name") ?? "";
    if (id == null) return;
    if (name == "SessionEnd") { sessions.Remove(id); Refresh2(); return; }
    // idle_prompt = ociosa ha ~60s; rede de seguranca caso a deteccao pelo transcript falhe
    string state;
    if (Str(e, "notification_type") == "idle_prompt") state = "idle";
    else if (!STATE.TryGetValue(name, out state)) return;
    Session s;
    if (!sessions.TryGetValue(id, out s)) {
      s = new Session { Transcript = Str(e, "transcript_path") };
      try { s.Offset = new FileInfo(s.Transcript).Length; } catch { s.Transcript = null; }
      sessions[id] = s;
    }
    s.Cwd = Str(e, "cwd") ?? s.Cwd ?? "?";
    s.State = state;
    s.At = DateTime.Now;
    Refresh2();
  }

  void CheckTranscripts() {
    bool changed = false;
    foreach (var s in sessions.Values) {
      if (s.Transcript == null) continue;
      try {
        using (var f = new FileStream(s.Transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
          if (f.Length <= s.Offset) continue;
          f.Position = s.Offset;
          var b = new byte[f.Length - s.Offset];
          int n = 0;
          while (n < b.Length) { int r = f.Read(b, n, b.Length - n); if (r <= 0) break; n += r; }
          int last = Array.LastIndexOf(b, (byte)'\n', n - 1);
          if (last < 0) continue; // linha pela metade fica pra proxima
          s.Offset += last + 1;
          foreach (var line in Encoding.UTF8.GetString(b, 0, last + 1).Split('\n')) {
            if (!line.Contains("[Request interrupted by user")) continue; // filtro barato antes do parse
            try {
              var m = Json().Deserialize<Dictionary<string, object>>(line);
              object msg, content;
              if (Str(m, "type") != "user" || !m.TryGetValue("message", out msg) || !((IDictionary<string, object>)msg).TryGetValue("content", out content)) continue;
              var list = content as ArrayList;
              var text = content as string ?? (list != null && list.Count > 0 ? Str(list[0] as Dictionary<string, object>, "text") : null);
              if (text != null && text.StartsWith("[Request interrupted by user")) { s.State = "idle"; s.At = DateTime.Now; changed = true; }
            } catch { }
          }
        }
      } catch { }
    }
    if (changed) Refresh2();
  }

  static string Folder(Session s) { return Path.GetFileName(s.Cwd.TrimEnd('/', '\\')); }

  void Refresh2() {
    rows = sessions.Values.OrderBy(s => Array.IndexOf(ORDER, s.State)).ThenBy(s => Folder(s), StringComparer.CurrentCultureIgnoreCase).ToList();
    tray.Icon = icons[ORDER.FirstOrDefault(st => rows.Any(r => r.State == st)) ?? "none"];
    var text = string.Join(" · ", ORDER.Select(st => rows.Count(r => r.State == st) + " " + T[st][1]));
    tray.Text = text.Length > 63 ? text.Substring(0, 63) : text; // limite do Windows
    var corner = new Point(Right, Bottom);
    Fit();
    if (IsHandleCreated) Location = new Point(corner.X - Width, corner.Y - Height); // cresce pra cima e pra esquerda
    Invalidate();
  }

  // ---------- widget ----------

  string None { get { return L("no sessions", "nenhuma sessao"); } }
  string Time(Session s) { return s.At.ToString("T", culture); }
  int MaxWidth(Func<Session, string> text) { return rows.Count == 0 ? 0 : rows.Max(r => TextRenderer.MeasureText(text(r), Font).Width); }

  void Fit() {
    int w = rows.Count == 0
      ? TextRenderer.MeasureText(None, Font).Width
      : DOT + 6 + MaxWidth(Folder) + 12 + MaxWidth(r => T[r.State][0]) + 12 + MaxWidth(Time);
    ClientSize = new Size(Math.Max(140, w + PAD * 2), PAD * 2 + Math.Max(1, rows.Count) * ROW - 6);
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    if (rows.Count == 0) { TextRenderer.DrawText(g, None, Font, new Point(PAD, PAD), Color.Gray); return; }
    int nameW = MaxWidth(Folder), labelW = MaxWidth(r => T[r.State][0]);
    for (int i = 0; i < rows.Count; i++) {
      var r = rows[i];
      int y = PAD + i * ROW;
      var color = COLORS[r.State];
      using (var b = new SolidBrush(color)) g.FillEllipse(b, PAD, y + 2, DOT, DOT);
      TextRenderer.DrawText(g, Folder(r), Font, new Point(PAD + DOT + 6, y), Color.Gainsboro);
      TextRenderer.DrawText(g, T[r.State][0], Font, new Point(PAD + DOT + 6 + nameW + 12, y), color);
      TextRenderer.DrawText(g, Time(r), Font, new Point(PAD + DOT + 6 + nameW + 12 + labelW + 12, y), Color.Gray);
    }
  }

  static Icon DotIcon(Color c) {
    var bmp = new Bitmap(16, 16);
    using (var g = Graphics.FromImage(bmp)) {
      g.SmoothingMode = SmoothingMode.AntiAlias;
      using (var b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 14, 14);
    }
    return Icon.FromHandle(bmp.GetHicon()); // ponytail: 4 icones criados uma vez so, sem DestroyIcon
  }

  // ---------- instalacao dos hooks em ~/.claude/settings.json ----------

  static Dictionary<string, object> ReadSettings() {
    if (!File.Exists(SETTINGS)) return new Dictionary<string, object>();
    var txt = File.ReadAllText(SETTINGS);
    return txt.Trim() == "" ? new Dictionary<string, object>() : Json().Deserialize<Dictionary<string, object>>(txt);
  }

  static object Get(IDictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }

  static bool IsOurs(object block) {
    var hooks = Get(block as Dictionary<string, object>, "hooks") as ArrayList;
    return hooks != null && hooks.Cast<object>().Any(h => {
      var d = h as Dictionary<string, object>;
      return Str(d, "type") == "http" && OUR_URL.IsMatch(Str(d, "url") ?? "");
    });
  }

  // null = settings.json invalido
  List<string> Problems(out string error) {
    error = null;
    Dictionary<string, object> cfg;
    try { cfg = ReadSettings(); } catch (Exception e) { error = e.Message; return null; }
    var problems = new List<string>();
    if (Get(cfg, "disableAllHooks") as bool? == true) problems.Add(L("\"disableAllHooks\": true turns off every hook", "\"disableAllHooks\": true desliga todos os hooks"));
    var hooks = Get(cfg, "hooks") as Dictionary<string, object>;
    foreach (var ev in EVENTS) {
      var list = Get(hooks, ev) as ArrayList;
      var block = list == null ? null : list.Cast<object>().FirstOrDefault(IsOurs) as Dictionary<string, object>;
      string want, have = block == null ? null : Str(block, "matcher") ?? "";
      MATCHERS.TryGetValue(ev, out want);
      want = want ?? "";
      if (block == null) problems.Add(L(ev + ": hook missing", ev + ": hook faltando"));
      else if (have != want) problems.Add(L(ev + ": matcher should be \"" + want + "\", found \"" + have + "\"", ev + ": matcher deveria ser \"" + want + "\", esta \"" + have + "\""));
    }
    return problems;
  }

  void Install() {
    var cfg = ReadSettings(); // ja validado pelo Problems()
    var hooks = Get(cfg, "hooks") as Dictionary<string, object>;
    if (hooks == null) cfg["hooks"] = hooks = new Dictionary<string, object>();
    foreach (var ev in EVENTS) {
      var list = Get(hooks, ev) as ArrayList;
      if (list == null) hooks[ev] = list = new ArrayList();
      if (list.Cast<object>().Any(IsOurs)) continue; // matcher errado: mostrado no aviso, nao mexe no que o usuario escreveu
      var block = new Dictionary<string, object>();
      string m;
      if (MATCHERS.TryGetValue(ev, out m)) block["matcher"] = m;
      block["hooks"] = new ArrayList { new Dictionary<string, object> { { "type", "http" }, { "url", "http://127.0.0.1:" + PORT }, { "timeout", 2 } } };
      list.Add(block);
    }
    Directory.CreateDirectory(Path.GetDirectoryName(SETTINGS));
    if (File.Exists(SETTINGS)) File.Copy(SETTINGS, SETTINGS + ".bak", true); // nunca perde a config do usuario
    var sb = new StringBuilder();
    WriteJson(sb, cfg, "");
    File.WriteAllText(SETTINGS, sb.Append('\n').ToString(), new UTF8Encoding(false));
  }

  // quiet = ao abrir o app: so fala alguma coisa se tiver problema
  void CheckInstall(bool quiet) {
    string error;
    var problems = Problems(out error);
    var title = "Traffic Light";
    if (problems == null) {
      MessageBox.Show(L("Could not read " + SETTINGS + " (invalid JSON). Fix it first:\n\n", "Nao consegui ler " + SETTINGS + " (JSON invalido). Corrija antes:\n\n") + error, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
      return;
    }
    if (problems.Count == 0) {
      if (!quiet) MessageBox.Show(L("All good: hooks installed in ", "Tudo certo: hooks instalados em ") + SETTINGS, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
      return;
    }
    var msg = L("Claude Code hooks need attention:\n\n", "Os hooks do Claude Code precisam de ajuste:\n\n") + "- " + string.Join("\n- ", problems)
      + L("\n\nInstall missing hooks now? (backup: settings.json.bak)", "\n\nInstalar os hooks que faltam agora? (backup: settings.json.bak)");
    if (MessageBox.Show(msg, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
    try { Install(); } catch (Exception e) { MessageBox.Show(e.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
    var left = Problems(out error);
    MessageBox.Show(left != null && left.Count == 0
      ? L("Hooks installed. Restart open Claude Code sessions to pick them up.", "Hooks instalados. Reinicie as sessoes abertas do Claude Code para valerem.")
      : L("Still needs a manual fix:\n\n- ", "Ainda precisa de ajuste manual:\n\n- ") + string.Join("\n- ", left ?? new List<string> { error }),
      title, MessageBoxButtons.OK, left != null && left.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
  }

  // JavaScriptSerializer so escreve tudo numa linha: aqui sai indentado como o Claude Code grava
  static void WriteJson(StringBuilder sb, object v, string ind) {
    var d = v as IDictionary<string, object>;
    var l = v as IList;
    if (v == null) sb.Append("null");
    else if (v is string) Quote(sb, (string)v);
    else if (v is bool) sb.Append((bool)v ? "true" : "false");
    else if (d != null) {
      if (d.Count == 0) { sb.Append("{}"); return; }
      sb.Append("{\n");
      int i = 0;
      foreach (var kv in d) {
        sb.Append(ind + "  ");
        Quote(sb, kv.Key);
        sb.Append(": ");
        WriteJson(sb, kv.Value, ind + "  ");
        sb.Append(++i < d.Count ? ",\n" : "\n");
      }
      sb.Append(ind + "}");
    } else if (l != null) {
      if (l.Count == 0) { sb.Append("[]"); return; }
      sb.Append("[\n");
      for (int i = 0; i < l.Count; i++) {
        sb.Append(ind + "  ");
        WriteJson(sb, l[i], ind + "  ");
        sb.Append(i + 1 < l.Count ? ",\n" : "\n");
      }
      sb.Append(ind + "]");
    } else sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); // numeros
  }

  static void Quote(StringBuilder sb, string s) {
    sb.Append('"');
    foreach (var ch in s) {
      switch (ch) {
        case '"': sb.Append("\\\""); break;
        case '\\': sb.Append("\\\\"); break;
        case '\n': sb.Append("\\n"); break;
        case '\r': sb.Append("\\r"); break;
        case '\t': sb.Append("\\t"); break;
        default:
          if (ch < 0x20) sb.AppendFormat("\\u{0:x4}", (int)ch); else sb.Append(ch);
          break;
      }
    }
    sb.Append('"');
  }

  protected override void OnFormClosed(FormClosedEventArgs e) {
    tray.Visible = false;
    listener.Stop();
    base.OnFormClosed(e);
  }

  [STAThread]
  static void Main(string[] args) {
    Application.EnableVisualStyles();
    var app = new TrafficLight(args.Contains("--ptbr"));
    try { app.listener.Start(); } catch (SocketException) {
      MessageBox.Show(app.L("Port " + PORT + " is already in use: another traffic light (exe or traffic_light.js) is probably running.",
        "A porta " + PORT + " ja esta em uso: provavelmente outro semaforo (exe ou traffic_light.js) esta rodando."), "Traffic Light", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      return;
    }
    Application.Run(app);
  }
}
