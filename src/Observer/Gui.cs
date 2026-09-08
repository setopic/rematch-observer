using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RematchObserver;

/// <summary>
/// 一般の利用者向けの画面。**要るのは 4 つだけ**（設定を作る / 確かめる / 常駐 / 設定の編集）。
///
/// **校正の道具（capture / crop / slice / match / read）はここに出さない。**
/// あれはテンプレートを直す人のもので、**普段動かす人には要らない。**
/// CLI に残してある。
///
/// ⚠ **Webhook の URL を画面に出さない。** 打ち込む欄は伏せ字にし、
/// **すでに入っているものは有無と長さだけ見せる**（<see cref="Config.Redacted"/> と同じ）。
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class MainForm : Form
{
    private const int Pad = 12, LabelW = 128, LineH = 26;

    private readonly TextBox _webhook = new() { UseSystemPasswordChar = true };
    private readonly Label _webhookState = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly TextBox _sender = new();
    private readonly TextBox _matchId = new();
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = Color.FromArgb(28, 30, 36),
        ForeColor = Color.Gainsboro,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };
    private readonly Button _save = new() { Text = "設定を保存" };
    private readonly Button _check = new() { Text = "確認する" };
    private readonly Button _start = new() { Text = "常駐を開始" };
    private readonly Button _stop = new() { Text = "停止", Enabled = false };

    private readonly string _configPath;
    private Config _config;
    private CancellationTokenSource? _running;

    public MainForm(string configPath)
    {
        _configPath = configPath;
        _config = Config.Load(configPath);

        Text = "rematch-observer";
        MinimumSize = new Size(620, 560);
        Size = new Size(620, 620);
        StartPosition = FormStartPosition.CenterScreen;

        int y = Pad;
        Add(new Label { Text = "画面を読んで、数字だけを Discord に送ります。画像は送りません。",
                        AutoSize = true, Location = new Point(Pad, y) });
        y += LineH;

        y = Field(y, "Webhook の URL", _webhook);
        _webhookState.Location = new Point(Pad + LabelW, y);
        Add(_webhookState);
        y += 20;
        y = Field(y, "自分の Discord id", _sender);
        y = Field(y, "対戦 id（運営のみ）", _matchId);

        y += 6;
        Row(y, _save, _check, _start, _stop);
        y += LineH + 10;

        Add(new Label { Text = "経過", AutoSize = true, Location = new Point(Pad, y) });
        y += 18;
        _log.Location = new Point(Pad, y);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Add(_log);
        Resize += (_, _) => _log.Size = new Size(ClientSize.Width - Pad * 2, ClientSize.Height - y - Pad);
        _log.Size = new Size(ClientSize.Width - Pad * 2, ClientSize.Height - y - Pad);

        _save.Click += (_, _) => Save();
        _check.Click += (_, _) => Check();
        _start.Click += (_, _) => Start();
        _stop.Click += (_, _) => Stop();
        FormClosing += (_, _) => Stop();

        Load += (_, _) => { Fill(); Check(); };
    }

    private void Add(Control c) => Controls.Add(c);

    private int Field(int y, string label, TextBox box)
    {
        Add(new Label { Text = label, AutoSize = true, Location = new Point(Pad, y + 3) });
        box.Location = new Point(Pad + LabelW, y);
        box.Width = 420;
        box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Add(box);
        return y + LineH;
    }

    private void Row(int y, params Button[] buttons)
    {
        int x = Pad + LabelW;
        foreach (var b in buttons)
        {
            b.Location = new Point(x, y);
            b.Width = 100;
            Add(b);
            x += 106;
        }
    }

    private void Fill()
    {
        _webhook.Text = "";
        _webhookState.Text = _config.WebhookUrl.Length > 0
            ? $"入っています（{_config.WebhookUrl.Length} 文字）。変えるときだけ上に貼ってください"
            : "未設定。運営から渡された URL を上に貼ってください";
        _sender.Text = _config.SenderDiscordId;
        _matchId.Text = _config.MatchId?.ToString() ?? "";
    }

    /// <summary>
    /// 設定を書く。**ファイルが無ければここで作られる**（CLI の `config-init` にあたる）。
    ///
    /// ⚠ **Webhook の欄が空なら、入っているものを消さない。**
    /// 画面に出していない以上、**空欄は「消したい」ではなく「触っていない」である。**
    /// </summary>
    private void Save()
    {
        if (_webhook.Text.Trim().Length > 0) _config.WebhookUrl = _webhook.Text.Trim();
        _config.SenderDiscordId = _sender.Text.Trim();
        _config.MatchId = int.TryParse(_matchId.Text.Trim(), out int id) ? id : null;
        try
        {
            _config.Save(_configPath);
            Write($"保存しました: {_configPath}");
        }
        catch (Exception e)
        {
            Write("保存できません: " + e.Message);
            Write("⚠ exe を Program Files に置くと、隣に書けないことがあります");
            return;
        }
        Fill();
        Check();
    }

    /// <summary>揃っているかを見る。**CLI の `check` と同じことを言う。**</summary>
    private void Check()
    {
        _config = Config.Load(_configPath);
        Write("― 確認 ―");
        Write($"設定       : {_configPath}");
        Write($"Webhook    : {Config.Redacted(_config.WebhookUrl)}");
        Write($"送り主     : {(_config.SenderDiscordId.Length > 0 ? _config.SenderDiscordId : "（未設定）")}");
        Write($"対戦 id    : {_config.MatchId?.ToString() ?? "（無し。選手は所属から引かれます）"}");

        var window = WindowFinder.Find(_config.ProcessName, _config.WindowTitle);
        Write($"ゲーム     : {(window?.ToString() ?? "（いま起動していません）")}");
        // ⚠ **`Capture` だけでは `Control.Capture`（マウスの捕捉）に取られる。**
        // Form が継承しているので、名前を省くと bool になる
        Write($"画面の取得 : {(RematchObserver.Capture.IsSupported ? "使えます" : "使えません")}");

        var templates = TemplateSet.Load(_config.ResolvedTemplatesDir);
        var missing = templates.Missing().ToList();
        Write($"テンプレート: ラベル {templates.Labels.Count} 枚 / 数字 {templates.Digits.Count} 枚"
            + $" / コードの数字 {templates.CodeDigits.Count} 枚");
        if (missing.Count > 0)
        {
            Write("  足りません: " + string.Join(", ", missing));
            Write("  ⚠ templates フォルダを exe と同じ場所に置いてください");
        }

        var problems = _config.Problems();
        foreach (var p in problems) Write("  ⚠ " + p);
        bool ready = problems.Count == 0 && missing.Count == 0;
        Write(ready ? "揃っています。常駐を開始できます" : "まだ足りません");
        _start.Enabled = ready && _running is null;
    }

    private void Start()
    {
        if (_running is not null) return;
        _config = Config.Load(_configPath);
        var templates = TemplateSet.Load(_config.ResolvedTemplatesDir);
        if (_config.Problems().Count > 0 || templates.Missing().Any()) { Check(); return; }

        _running = new CancellationTokenSource();
        _start.Enabled = false;
        _stop.Enabled = true;
        Write("― 常駐を開始しました ―");
        Write("⚠ 送るのは数字だけです。画像は保存も送信もしません");

        var webhook = new Webhook(_config.WebhookUrl);
        var watcher = new Watcher(_config, new ScreenReader(templates, _config), webhook, Write);
        var token = _running.Token;
        Task.Run(async () =>
        {
            try { await watcher.RunAsync(token); }
            catch (OperationCanceledException) { }
            catch (Exception e) { Write("止まりました: " + e.Message); }
            finally
            {
                webhook.Dispose();
                Post(() =>
                {
                    _running = null;
                    _stop.Enabled = false;
                    _start.Enabled = true;
                    Write("― 常駐を終えました ―");
                });
            }
        }, token);
    }

    private void Stop()
    {
        if (_running is null) return;
        _running.Cancel();
        _stop.Enabled = false;
    }

    /// <summary>
    /// 1 行書く。**常駐の側から呼ばれるので、画面の担当に渡し直す。**
    /// **溜まり続けないように古い行を捨てる。**
    /// </summary>
    private void Write(string line) => Post(() =>
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        var lines = _log.Lines.Append(stamped).ToArray();
        if (lines.Length > 500) lines = lines[^500..];
        _log.Lines = lines;
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    });

    private void Post(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}

/// <summary>画面を出す入口。</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static partial class Gui
{
    /// <summary>
    /// **引数なしで起動したときの入口。**
    ///
    /// ⚠ **CLI を残すために、この exe は WinExe である。**
    /// 引数付きで呼ばれたときは、**呼び出した端末のコンソールに繋ぎ直して**文字を出す
    /// （<see cref="Attach"/>）。**そうしないと `check` も `read` も無言になる。**
    /// </summary>
    public static int Run(string configPath)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(configPath));
        return 0;
    }

    private const int ParentProcess = -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    /// <summary>呼び出した端末に繋ぎ直す。**繋げなければ何もしない**（二重起動などで失敗しうる）。</summary>
    public static void Attach()
    {
        try { AttachConsole(ParentProcess); } catch (Exception) { /* 端末が無いだけ */ }
    }
}
