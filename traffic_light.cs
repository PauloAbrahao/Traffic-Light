// traffic_light.exe: semaforo do Claude Code autossuficiente (sem node). Icone na bandeja + widget flutuante.
//   traffic_light.exe [--ptbr]   (o idioma escolhido no menu fica salvo e vale sobre a flag)
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
  public string Id, State, Cwd, Transcript;
  public long Offset;
  public DateTime At; // ultima mudanca de estado
}

class TrafficLight : Form {
  const int PORT = 4545;
  const int PAD = 16, HEAD = 48, ROW = 36, FOOT = 44, BADGE = 22, BTN = 22;
  const int MINI = 36, MINI_PAD = 12, MINI_DOT = 10, MINI_STEP = 16; // recolhido: so as bolinhas
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
  static readonly Color BG = Color.FromArgb(0x18, 0x19, 0x1d), BORDER = Color.FromArgb(0x34, 0x35, 0x3a), LINE = Color.FromArgb(0x2c, 0x2d, 0x32),
    HOVER = Color.FromArgb(0x26, 0x27, 0x2c), TEXT = Color.FromArgb(0xe6, 0xe7, 0xea), MUTED = Color.FromArgb(0x8a, 0x8d, 0x93), ACCENT = Color.FromArgb(0x8b, 0x9c, 0xf7);
  static readonly string SETTINGS = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
  static readonly Regex OUR_URL = new Regex(@"^http://(127\.0\.0\.1|localhost):" + PORT + "/?$");

  [DllImport("user32.dll")] static extern bool ReleaseCapture();
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

  bool ptbr;
  Dictionary<string, string[]> T; // estado -> [rotulo, singular, plural]
  readonly Font fTitle = new Font("Segoe UI Semibold", 11), fName = new Font("Segoe UI", 10.5f), fBadge = new Font("Segoe UI Semibold", 8.5f), fSmall = new Font("Segoe UI", 9);
  readonly ContextMenuStrip rowMenu = new ContextMenuStrip();
  DateTime updated = DateTime.Now; // ultimo evento recebido
  string hot; // botao sob o mouse: "collapse", "expand", "menu", "row3"
  bool dwmCorners, collapsed, uninstalled;
  DotTip tip; // ToolTip do WinForms nao aparece: o widget nunca fica ativo
  // recolhido: dica so depois de 1,5s parado na bolinha, senao atrapalha arrastar
  readonly System.Windows.Forms.Timer hoverTimer = new System.Windows.Forms.Timer { Interval = 1500 };
  int hoverDot = -1;
  readonly NotifyIcon tray = new NotifyIcon();
  readonly Dictionary<string, Icon> icons = new Dictionary<string, Icon>();
  readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>(); // so mexida na thread da UI
  readonly TcpListener listener = new TcpListener(IPAddress.Loopback, PORT);
  List<Session> rows = new List<Session>();

  string L(string en, string pt) { return ptbr ? pt : en; }
  static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; } // PostToolUse pode ser grande
  static string Str(IDictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as string : null; }

  TrafficLight(bool ptbr) {
    FormBorderStyle = FormBorderStyle.None;
    TopMost = true;
    ShowInTaskbar = false;
    DoubleBuffered = true;
    StartPosition = FormStartPosition.Manual;
    BackColor = BG;
    Font = fName;

    foreach (var kv in COLORS) icons[kv.Key] = DotIcon(kv.Value);
    tray.Icon = icons["none"];
    tray.ContextMenuStrip = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkMenu()) };
    rowMenu.Renderer = tray.ContextMenuStrip.Renderer;
    SetLanguage(ptbr);
    tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Visible = !Visible; };

    tip = new DotTip(fSmall);
    hoverTimer.Tick += (s, e) => ShowDotTip();
    MouseDown += (s, e) => {
      HoverDot(-1);
      if (e.Button != MouseButtons.Left) return;
      var h = HotAt(e.Location);
      if (h == "collapse" || h == "expand") SetCollapsed(h == "collapse");
      else if (h == "menu") tray.ContextMenuStrip.Show(this, new Point(MenuRect.Left, MenuRect.Bottom + 4));
      else if (h != null) ShowRowMenu(int.Parse(h.Substring(3)));
      else { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); } // arrasta pelo widget inteiro (finge que clicou na barra de titulo)
    };
    MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) tray.ContextMenuStrip.Show(Cursor.Position); };
    MouseMove += (s, e) => { SetHot(HotAt(e.Location)); HoverDot(DotAt(e.Location)); };
    MouseLeave += (s, e) => { SetHot(null); HoverDot(-1); };
    VisibleChanged += (s, e) => HoverDot(-1);
  }

  protected override void OnLoad(EventArgs e) {
    base.OnLoad(e);
    try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(REG)) collapsed = k != null && k.GetValue("Collapsed") as int? == 1; } catch { }
    Refresh2();
    RestorePosition();
    tray.Visible = true;
    new Thread(Serve) { IsBackground = true }.Start();
    // Esc nao dispara hook nenhum, mas o Claude Code grava a interrupcao no transcript na hora
    var timer = new System.Windows.Forms.Timer { Interval = 1000 }; // ponytail: polling de 1s, FileSystemWatcher se precisar ser instantaneo
    timer.Tick += (s, ev) => { CheckTranscripts(); KeepOnTop(); if (Visible) Invalidate(); }; // "Atualizado ha" anda a cada 1s
    timer.Start();
    // so reposiciona quando a tela muda: checar a cada 1s brigava com o arraste (DPI misto, taskbar) e prendia no canto
    Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    ResizeEnd += (s, ev) => SavePosition(); // dispara no fim do arraste tambem
    BeginInvoke((Action)(() => CheckInstall(true)));
  }

  const string REG = @"Software\TrafficLight";

  // idioma escolhido no menu: null = nunca escolhido, vale a flag --ptbr
  static bool? SavedLanguage() {
    try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(REG)) { var v = k == null ? null : k.GetValue("Language") as string; return v == null ? (bool?)null : v == "pt-BR"; } } catch { return null; }
  }

  void SetLanguage(bool pt) {
    ptbr = pt;
    T = ptbr
      ? new Dictionary<string, string[]> { { "waiting", new[] { "AGUARDANDO", "alerta", "alertas" } }, { "busy", new[] { "ATIVO", "ativo", "ativos" } }, { "idle", new[] { "OCIOSO", "ocioso", "ociosos" } } }
      : new Dictionary<string, string[]> { { "waiting", new[] { "NEEDS INPUT", "alert", "alerts" } }, { "busy", new[] { "WORKING", "working", "working" } }, { "idle", new[] { "IDLE", "idle", "idle" } } };
    var items = tray.ContextMenuStrip.Items;
    items.Clear();
    items.Add(L("Show/hide widget", "Mostrar/ocultar widget"), null, (s, e) => Visible = !Visible);
    items.Add(L("Check installation", "Verificar instalação"), null, (s, e) => CheckInstall(false));
    items.Add(L("Uninstall (remove hooks)", "Desinstalar (remover hooks)"), null, (s, e) => Uninstall());
    items.Add(new ToolStripSeparator());
    // check desenhado no texto: o glifo do ToolStrip some no fundo escuro
    items.Add((ptbr ? "    " : "✓  ") + "English", null, (s, e) => ChooseLanguage(false));
    items.Add((ptbr ? "✓  " : "    ") + "Português", null, (s, e) => ChooseLanguage(true));
    items.Add(new ToolStripSeparator());
    items.Add(L("Exit", "Sair"), null, (s, e) => Close());
    foreach (ToolStripItem it in items) it.ForeColor = TEXT;
  }

  void ChooseLanguage(bool pt) {
    try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(REG)) k.SetValue("Language", pt ? "pt-BR" : "en-US"); } catch { }
    if (pt == ptbr) return;
    SetLanguage(pt);
    Refresh2(); // textos mudam de largura
  }

  // posicao guardada pelo canto de baixo/direita: o widget cresce pra cima e pra esquerda
  void RestorePosition() {
    object x = null, y = null;
    try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(REG)) if (k != null) { x = k.GetValue("Right"); y = k.GetValue("Bottom"); } } catch { }
    if (x is int && y is int) Location = new Point((int)x - Width, (int)y - Height);
    else {
      var area = Screen.PrimaryScreen.WorkingArea; // primeira vez: canto de baixo, a direita
      Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
    }
    KeepInside();
  }

  void SavePosition() {
    try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(REG)) { k.SetValue("Right", Right); k.SetValue("Bottom", Bottom); } } catch { }
  }

  // monitor desconectado / resolucao mudando pode deixar a janela fora da tela:
  // empurra so o necessario pra dentro do monitor mais proximo, sem mandar pro canto
  void KeepInside() {
    var area = Screen.FromRectangle(Bounds).WorkingArea;
    int x = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
    int y = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
    if (x != Left || y != Top) Location = new Point(x, y);
  }

  void OnDisplayChanged(object sender, EventArgs e) {
    BeginInvoke((Action)KeepInside);
  }

  // o Windows perde o "sempre no topo" (Explorer reiniciando, tela cheia, RDP).
  // TopMost = true nao reaplica se ja for true, entao vai direto no SetWindowPos.
  void KeepOnTop() {
    if (!Visible) return;
    SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOPMOST; NOSIZE | NOMOVE | NOACTIVATE
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
    updated = DateTime.Now;
    if (name == "SessionEnd") { sessions.Remove(id); Refresh2(); return; }
    // idle_prompt = ociosa ha ~60s; rede de seguranca caso a deteccao pelo transcript falhe
    string state;
    if (Str(e, "notification_type") == "idle_prompt") state = "idle";
    else if (!STATE.TryGetValue(name, out state)) return;
    Session s;
    if (!sessions.TryGetValue(id, out s)) {
      s = new Session { Id = id, Transcript = Str(e, "transcript_path") };
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
              if (text != null && text.StartsWith("[Request interrupted by user")) { s.State = "idle"; s.At = updated = DateTime.Now; changed = true; }
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
    var text = Summary();
    tray.Text = text.Length > 63 ? text.Substring(0, 63) : text; // limite do Windows
    var corner = new Point(Right, Bottom);
    Fit();
    if (IsHandleCreated) Location = new Point(corner.X - Width, corner.Y - Height); // cresce pra cima e pra esquerda
    Invalidate();
  }

  // ---------- widget ----------

  string Title { get { return L("AGENT MONITOR", "MONITOR DE AGENTES"); } }
  const TextFormatFlags LEFT = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
  const TextFormatFlags RIGHT = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Right;

  string None { get { return L("no sessions", "nenhuma sessão"); } }
  string Count() { return rows.Count + (rows.Count == 1 ? L(" session", " sessão") : L(" sessions", " sessões")); }
  string Summary() {
    return string.Join(" · ", new[] { "busy", "idle", "waiting" }.Select(st => {
      int n = rows.Count(r => r.State == st);
      return n + " " + T[st][n == 1 ? 1 : 2];
    }));
  }
  string Updated() {
    var d = DateTime.Now - updated;
    if (d.TotalSeconds < 10) return L("Updated just now", "Atualizado agora");
    if (d.TotalSeconds < 60) return L("Updated " + (int)d.TotalSeconds + "s ago", "Atualizado há " + (int)d.TotalSeconds + "s");
    if (d.TotalMinutes < 60) return L("Updated " + (int)d.TotalMinutes + " min ago", "Atualizado há " + (int)d.TotalMinutes + " min");
    return L("Updated " + (int)d.TotalHours + " h ago", "Atualizado há " + (int)d.TotalHours + " h");
  }

  static int Measure(string t, Font f) { return TextRenderer.MeasureText(t, f, Size.Empty, TextFormatFlags.NoPadding).Width; }
  int MaxWidth(Func<Session, string> text, Font f) { return rows.Count == 0 ? 0 : rows.Max(r => Measure(text(r), f)); }
  int BadgeWidth(Session r) { return Measure(T[r.State][0], fBadge) + 20; }
  static Color Mix(Color a, Color b, double t) { return Color.FromArgb((int)(a.R * t + b.R * (1 - t)), (int)(a.G * t + b.G * (1 - t)), (int)(a.B * t + b.B * (1 - t))); }

  int RowTop(int i) { return HEAD + 8 + i * ROW; }
  int FootTop { get { return RowTop(Math.Max(1, rows.Count)) + 8; } }
  Rectangle MenuRect { get { return new Rectangle(ClientSize.Width - PAD - BTN + 4, (HEAD - BTN) / 2, BTN, BTN); } }
  Rectangle CollapseRect { get { var r = MenuRect; r.Offset(-BTN - 4, 0); return r; } }
  Rectangle ExpandRect { get { return new Rectangle(ClientSize.Width - MINI_PAD - BTN + 4, (MINI - BTN) / 2, BTN, BTN); } }
  Rectangle RowMenuRect(int i) { return new Rectangle(ClientSize.Width - PAD - BTN + 4, RowTop(i) + (ROW - BTN) / 2, BTN, BTN); }

  string HotAt(Point p) {
    if (collapsed) return ExpandRect.Contains(p) ? "expand" : null;
    if (MenuRect.Contains(p)) return "menu";
    if (CollapseRect.Contains(p)) return "collapse";
    for (int i = 0; i < rows.Count; i++) if (RowMenuRect(i).Contains(p)) return "row" + i;
    return null;
  }

  Rectangle DotRect(int i) { return new Rectangle(MINI_PAD + i * MINI_STEP - 3, MINI / 2 - MINI_DOT / 2 - 3, MINI_DOT + 6, MINI_DOT + 6); }

  int DotAt(Point p) {
    if (!collapsed) return -1;
    for (int i = 0; i < Math.Max(1, rows.Count); i++) if (DotRect(i).Contains(p)) return i;
    return -1;
  }

  // mudou de bolinha (ou saiu): zera a espera e some com a dica
  void HoverDot(int i) {
    if (i == hoverDot) return;
    hoverDot = i;
    hoverTimer.Stop();
    tip.Hide();
    if (i >= 0) hoverTimer.Start();
  }

  void ShowDotTip() {
    hoverTimer.Stop();
    if (!collapsed || hoverDot < 0) return;
    var text = hoverDot < rows.Count ? Folder(rows[hoverDot]) + "  " + T[rows[hoverDot].State][0] : None;
    var below = PointToScreen(new Point(DotRect(hoverDot).Left, MINI + 6));
    tip.ShowAt(text, below, Top - 6); // sem espaco embaixo, abre em cima
  }

  void SetHot(string h) {
    if (h == hot) return;
    hot = h;
    Cursor = h == null ? Cursors.Default : Cursors.Hand;
    Invalidate();
  }

  void ShowRowMenu(int i) {
    var s = rows[i];
    rowMenu.Items.Clear();
    rowMenu.Items.Add(L("Copy path", "Copiar caminho"), null, (o, e) => { try { Clipboard.SetText(s.Cwd); } catch { } });
    rowMenu.Items.Add(L("Remove from list", "Remover da lista"), null, (o, e) => { sessions.Remove(s.Id); Refresh2(); });
    foreach (ToolStripItem it in rowMenu.Items) it.ForeColor = TEXT;
    var r = RowMenuRect(i);
    rowMenu.Show(this, new Point(r.Left, r.Bottom + 4));
  }

  // recolhe/expande como lista suspensa: o canto de cima a direita fica parado
  void SetCollapsed(bool c) {
    var topRight = new Point(Right, Top);
    collapsed = c;
    try { using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(REG)) k.SetValue("Collapsed", c ? 1 : 0); } catch { }
    hot = null;
    HoverDot(-1);
    Cursor = Cursors.Default;
    Fit();
    Location = new Point(topRight.X - Width, topRight.Y);
    KeepInside();
    SavePosition();
    Invalidate();
  }

  void Fit() {
    if (collapsed) { ClientSize = new Size(MINI_PAD + Math.Max(1, rows.Count) * MINI_STEP - (MINI_STEP - MINI_DOT) + 8 + BTN + MINI_PAD - 4, MINI); return; }
    int rowW = rows.Count == 0
      ? PAD + Measure(None, fName) + PAD
      : PAD + 16 + MaxWidth(Folder, fName) + 24 + rows.Max(r => BadgeWidth(r)) + 8 + BTN + PAD;
    int headW = PAD + 26 + Measure(Title, fTitle) + 24 + Measure(Count(), fSmall) + 12 + BTN * 2 + 4 + PAD;
    int footW = PAD + Measure(Summary(), fSmall) + 24 + Measure(L("Updated 59 min ago", "Atualizado há 59 min"), fSmall) + PAD;
    ClientSize = new Size(Math.Max(360, Math.Max(rowW, Math.Max(headW, footW))), FootTop + FOOT);
  }

  // Windows 11 arredonda e pinta a borda pelo DWM; no 10 cai pra Region (sem antialias)
  protected override void OnHandleCreated(EventArgs e) {
    base.OnHandleCreated(e);
    try {
      int round = 2, border = BORDER.R | BORDER.G << 8 | BORDER.B << 16;
      dwmCorners = DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0; // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
      if (dwmCorners) DwmSetWindowAttribute(Handle, 34, ref border, 4);  // DWMWA_BORDER_COLOR
    } catch { }
    UpdateShape();
  }

  protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateShape(); }

  // OnResize roda durante a criacao do handle, antes do dwmCorners: com DWM, limpa a Region que tenha ficado
  void UpdateShape() {
    if (!IsHandleCreated) return;
    var old = Region;
    if (dwmCorners) { if (old == null) return; Region = null; }
    else using (var path = RoundRect(new RectangleF(0, 0, Width, Height), 10)) Region = new Region(path);
    if (old != null) old.Dispose();
  }

  static GraphicsPath RoundRect(RectangleF r, float rad) {
    var p = new GraphicsPath();
    float d = rad * 2;
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
  }

  static void Fill(Graphics g, Color c, RectangleF r, float rad) {
    using (var b = new SolidBrush(c)) using (var p = RoundRect(r, rad)) g.FillPath(b, p);
  }

  void HoverBg(Graphics g, string id, Rectangle r) { if (hot == id) Fill(g, HOVER, r, 6); }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias;
    int w = ClientSize.Width;
    using (var muted = new Pen(MUTED, 1.5f)) using (var line = new Pen(LINE)) {
      if (!dwmCorners) using (var p = new Pen(BORDER)) using (var path = RoundRect(new RectangleF(0.5f, 0.5f, w - 1, ClientSize.Height - 1), 10)) g.DrawPath(p, path);

      if (collapsed) {
        int my = MINI / 2;
        if (rows.Count == 0) using (var b = new SolidBrush(Mix(MUTED, BG, 0.5))) g.FillEllipse(b, MINI_PAD, my - MINI_DOT / 2, MINI_DOT, MINI_DOT);
        for (int i = 0; i < rows.Count; i++)
          using (var b = new SolidBrush(COLORS[rows[i].State])) g.FillEllipse(b, MINI_PAD + i * MINI_STEP, my - MINI_DOT / 2, MINI_DOT, MINI_DOT);
        var ex = ExpandRect;
        HoverBg(g, "expand", ex);
        int ecx = ex.Left + BTN / 2;
        g.DrawLines(muted, new[] { new PointF(ecx - 4, my - 2), new PointF(ecx, my + 2), new PointF(ecx + 4, my - 2) });
        return;
      }

      // cabecalho: icone + titulo, contagem, ocultar, menu
      int cy = HEAD / 2;
      using (var accent = new Pen(ACCENT, 1.5f)) {
        using (var path = RoundRect(new RectangleF(PAD + 0.5f, cy - 7, 14, 14), 3)) g.DrawPath(accent, path);
        g.DrawLines(accent, new[] { new PointF(PAD + 3.5f, cy + 3), new PointF(PAD + 6, cy), new PointF(PAD + 8.5f, cy + 2), new PointF(PAD + 11.5f, cy - 3) });
      }
      TextRenderer.DrawText(g, Title, fTitle, new Rectangle(PAD + 26, 0, w, HEAD), Color.White, LEFT);
      var hide = CollapseRect;
      TextRenderer.DrawText(g, Count(), fSmall, new Rectangle(0, 0, hide.Left - 8, HEAD), MUTED, RIGHT);
      HoverBg(g, "collapse", hide);
      g.DrawLine(muted, hide.Left + 6, cy, hide.Right - 6, cy);
      var menu = MenuRect;
      HoverBg(g, "menu", menu);
      int mx = menu.Left + BTN / 2;
      g.DrawLine(muted, mx - 6, cy - 3, mx + 6, cy - 3);
      g.DrawLine(muted, mx - 6, cy + 3, mx + 6, cy + 3);
      using (var b = new SolidBrush(hot == "menu" ? HOVER : BG)) {
        g.FillEllipse(b, mx + 0.5f, cy - 5, 4, 4); g.DrawEllipse(muted, mx + 0.5f, cy - 5, 4, 4);
        g.FillEllipse(b, mx - 4.5f, cy + 1, 4, 4); g.DrawEllipse(muted, mx - 4.5f, cy + 1, 4, 4);
      }
      g.DrawLine(line, PAD, HEAD, w - PAD, HEAD);

      // sessoes: ponto, pasta, badge, tempo no estado, menu da linha
      if (rows.Count == 0) TextRenderer.DrawText(g, None, fName, new Rectangle(PAD, RowTop(0), w, ROW), MUTED, LEFT);
      for (int i = 0; i < rows.Count; i++) {
        var r = rows[i];
        var color = COLORS[r.State];
        int top = RowTop(i), mid = top + ROW / 2;
        var dots = RowMenuRect(i);
        int badgeW = BadgeWidth(r), badgeX = dots.Left - 8 - badgeW;
        using (var b = new SolidBrush(Mix(color, BG, 0.45))) g.FillEllipse(b, PAD + 1, mid - 3, 6, 6);
        TextRenderer.DrawText(g, Folder(r), fName, new Rectangle(PAD + 16, top, badgeX - PAD - 16 - 12, ROW), TEXT, LEFT);
        var badge = new Rectangle(badgeX, mid - BADGE / 2, badgeW, BADGE);
        Fill(g, Mix(color, BG, 0.16), badge, 6);
        TextRenderer.DrawText(g, T[r.State][0], fBadge, badge, color, TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        HoverBg(g, "row" + i, dots);
        using (var b = new SolidBrush(MUTED)) for (int k = -1; k <= 1; k++) g.FillEllipse(b, dots.Left + BTN / 2 + k * 5 - 1.5f, mid - 1.5f, 3, 3);
      }

      // rodape: resumo + ultima atualizacao
      int foot = FootTop;
      g.DrawLine(line, PAD, foot, w - PAD, foot);
      TextRenderer.DrawText(g, Summary(), fSmall, new Rectangle(PAD, foot, w, FOOT), MUTED, LEFT);
      TextRenderer.DrawText(g, Updated(), fSmall, new Rectangle(0, foot, w - PAD, FOOT), ACCENT, RIGHT);
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

  static bool IsOurHook(object h) {
    var d = h as Dictionary<string, object>;
    return Str(d, "type") == "http" && OUR_URL.IsMatch(Str(d, "url") ?? "");
  }

  static bool IsOurs(object block) {
    var hooks = Get(block as Dictionary<string, object>, "hooks") as ArrayList;
    return hooks != null && hooks.Cast<object>().Any(IsOurHook);
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
    SaveSettings(cfg);
  }

  static void SaveSettings(Dictionary<string, object> cfg) {
    Directory.CreateDirectory(Path.GetDirectoryName(SETTINGS));
    if (File.Exists(SETTINGS)) File.Copy(SETTINGS, SETTINGS + ".bak", true); // nunca perde a config do usuario
    var sb = new StringBuilder();
    WriteJson(sb, cfg, "");
    File.WriteAllText(SETTINGS, sb.Append('\n').ToString(), new UTF8Encoding(false));
  }

  // tira so os nossos hooks: hook de outra ferramenta no mesmo bloco fica; bloco/evento vazio sai
  static int RemoveHooks(Dictionary<string, object> cfg) {
    var hooks = Get(cfg, "hooks") as Dictionary<string, object>;
    if (hooks == null) return 0;
    int removed = 0;
    foreach (var ev in hooks.Keys.ToList()) {
      var list = hooks[ev] as ArrayList;
      if (list == null) continue;
      for (int i = list.Count - 1; i >= 0; i--) {
        var inner = Get(list[i] as Dictionary<string, object>, "hooks") as ArrayList;
        if (inner == null) continue;
        int before = inner.Count;
        for (int j = inner.Count - 1; j >= 0; j--) if (IsOurHook(inner[j])) inner.RemoveAt(j);
        removed += before - inner.Count;
        if (before > 0 && inner.Count == 0) list.RemoveAt(i);
      }
      if (list.Count == 0) hooks.Remove(ev);
    }
    if (hooks.Count == 0) cfg.Remove("hooks");
    return removed;
  }

  void Uninstall() {
    var title = "Traffic Light";
    if (MessageBox.Show(L("Remove the traffic light hooks from " + SETTINGS + "?\n\nOther hooks are kept (backup: settings.json.bak). The traffic light will close.",
        "Remover os hooks do semáforo de " + SETTINGS + "?\n\nOs outros hooks continuam (backup: settings.json.bak). O semáforo vai fechar."),
        title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
    int removed;
    try {
      var cfg = ReadSettings();
      removed = RemoveHooks(cfg);
      if (removed > 0) SaveSettings(cfg);
    } catch (Exception e) { MessageBox.Show(e.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
    uninstalled = true;
    try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(REG, false); } catch { } // posicao, idioma, recolhido
    MessageBox.Show(removed > 0
      ? L("Removed " + removed + " hooks. Restart open Claude Code sessions.\n\nTo finish, delete traffic_light.exe. To use it again, just open it and accept the install.",
          removed + " hooks removidos. Reinicie as sessões abertas do Claude Code.\n\nPara terminar, apague o traffic_light.exe. Para usar de novo, é só abrir e aceitar a instalação.")
      : L("No traffic light hooks found in " + SETTINGS + ".", "Nenhum hook do semáforo em " + SETTINGS + "."),
      title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    Close();
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

  // dica escura que aparece sem roubar o foco de quem esta digitando
  class DotTip : Form {
    string text = "";
    public DotTip(Font f) {
      FormBorderStyle = FormBorderStyle.None;
      ShowInTaskbar = false;
      StartPosition = FormStartPosition.Manual;
      TopMost = true;
      DoubleBuffered = true;
      BackColor = HOVER;
      Font = f;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x8 | 0x08000000; return cp; } // TOOLWINDOW | TOPMOST | NOACTIVATE
    }
    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
      try {
        int round = 3, border = BORDER.R | BORDER.G << 8 | BORDER.B << 16; // ROUNDSMALL
        DwmSetWindowAttribute(Handle, 33, ref round, 4);
        DwmSetWindowAttribute(Handle, 34, ref border, 4);
      } catch { }
    }
    public void ShowAt(string t, Point below, int aboveBottom) {
      text = t;
      var sz = TextRenderer.MeasureText(t, Font, Size.Empty, TextFormatFlags.NoPadding);
      ClientSize = new Size(sz.Width + 20, sz.Height + 12);
      var area = Screen.FromPoint(below).WorkingArea;
      Location = new Point(Math.Max(area.Left, Math.Min(below.X, area.Right - Width)), below.Y + Height <= area.Bottom ? below.Y : aboveBottom - Height);
      if (!Visible) Show();
      Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e) {
      TextRenderer.DrawText(e.Graphics, text, Font, ClientRectangle, TEXT, TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
  }

  class DarkMenu : ProfessionalColorTable {
    public override Color ToolStripDropDownBackground { get { return HOVER; } }
    public override Color ImageMarginGradientBegin { get { return HOVER; } }
    public override Color ImageMarginGradientMiddle { get { return HOVER; } }
    public override Color ImageMarginGradientEnd { get { return HOVER; } }
    public override Color MenuBorder { get { return BORDER; } }
    public override Color MenuItemBorder { get { return LINE; } }
    public override Color MenuItemSelected { get { return Color.FromArgb(0x33, 0x34, 0x3a); } }
    public override Color SeparatorDark { get { return LINE; } }
  }

  protected override void OnFormClosed(FormClosedEventArgs e) {
    if (!uninstalled) SavePosition();
    tray.Visible = false;
    Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
    listener.Stop();
    base.OnFormClosed(e);
  }

  [STAThread]
  static void Main(string[] args) {
    Application.EnableVisualStyles();
    var app = new TrafficLight(SavedLanguage() ?? args.Contains("--ptbr"));
    try { app.listener.Start(); } catch (SocketException) {
      MessageBox.Show(app.L("Port " + PORT + " is already in use: another traffic light (exe or traffic_light.js) is probably running.",
        "A porta " + PORT + " ja esta em uso: provavelmente outro semaforo (exe ou traffic_light.js) esta rodando."), "Traffic Light", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      return;
    }
    Application.Run(app);
  }
}
