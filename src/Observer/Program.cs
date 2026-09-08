using System.Runtime.Versioning;
using System.Text;

namespace RematchObserver;

/// <summary>
/// 入口。**校正のための小さな道具と、常駐が 1 つの exe に入っている。**
///
/// ⚠ **画像は外に出さない**（ADR-0067）。`capture` と `crop` が書くのは
/// **手元のファイルだけ**で、送るのは `send` / `watch` の**数字の 1 行だけ**である。
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class Program
{
    private const string Usage = """
        rematch-observer — Rematch の画面を読んで、数字だけを Discord の Webhook に送ります。

          windows                          撮れるウィンドウを並べる
          capture [--out <png>] [--delay <秒>] [--count <枚>] [--interval <秒>]
                                           ゲームのウィンドウを保存する
                                           （--delay で待ってから、--count で続けて）
          crop --in <png> --rect x,y,w,h --out <png>
                                           テンプレートを切り出す
          slice --in <png> --rect x,y,w,h --out <前置き>
                                           枠の中の数字を字ごとに切り分ける
          match --in <png>                 その画像でラベルがどこに当たるかを見る
          read  --in <png>                 その画像から値を読む（送らない）
          send  --code <6 桁> | --score <ホーム> <アウェイ>
                                           1 通だけ送る（疎通の確認）
          watch                            常駐する（既定）
          check                            設定とテンプレートの状態を見る
          config-init                      設定ファイルの雛形を作る

        共通のもの
          --config <path>   設定ファイル（既定: %APPDATA%\\rematch-observer\\config.json）
          --window <名前>   プロセス名で相手を選ぶ（既定は設定の processName）
          --side home|away  送る側（既定は - ＝ 分からない）
          --match <id>      対戦 id（運営には要る。対戦表の #12 の数字）
          --dry-run         送らずに本文だけ出す
        """;

    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { /* 出力先による */ }
        var a = new Args(args);
        string command = a.Command ?? "watch";
        try
        {
            return command switch
            {
                "windows" => Windows(),
                "capture" => await CaptureOne(a),
                "crop" => Crop(a),
                "slice" => Slice(a),
                "match" => Match(a),
                "read" => Read(a),
                "send" => await Send(a),
                "watch" => await Watch(a),
                "check" => Check(a),
                "config-init" => ConfigInit(a),
                "help" or "--help" or "-h" => Print(Usage),
                _ => Print($"知らないコマンドです: {command}\n\n{Usage}", 2),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("失敗しました: " + e.Message);
            return 1;
        }
    }

    private static int Print(string text, int code = 0) { Console.WriteLine(text); return code; }

    // ---- ウィンドウ ---------------------------------------------------------

    private static int Windows()
    {
        Console.WriteLine($"画面キャプチャ（WGC）が使えるか: {(Capture.IsSupported ? "はい" : "いいえ")}");
        Console.WriteLine("見えているウィンドウ:");
        Console.Write(WindowFinder.Describe(WindowFinder.List()));
        return 0;
    }

    private static WindowInfo Target(Args a, Config c)
    {
        var name = a.Value("--window") ?? c.ProcessName;
        var found = WindowFinder.Find(name, c.WindowTitle)
            ?? throw new InvalidOperationException(
                $"`{name}` に合うウィンドウがありません。`rematch-observer windows` で名前を確かめてください");
        return found;
    }

    private static async Task<int> CaptureOne(Args a)
    {
        var c = a.Config();
        var window = Target(a, c);
        Console.WriteLine($"対象: {window}");

        // **結果の画面は試合が終わった直後にしか出ない。**
        // コマンドを打つには別のウィンドウに移る必要があるので、
        // **打ってからゲームに戻る時間**と、**何枚か続けて撮る**手段が要る。
        int delay = Number(a.Value("--delay"), 0);
        int count = Math.Max(1, Number(a.Value("--count"), 1));
        int interval = Math.Max(1, Number(a.Value("--interval"), 3));
        var outPath = a.Value("--out") ?? $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.png";

        for (int left = delay; left > 0; left--)
        {
            if (left == delay) Console.WriteLine("ゲームの画面に戻ってください");
            Console.Write($"\r  あと {left} 秒  ");
            await Task.Delay(1000);
        }
        if (delay > 0) Console.WriteLine();

        for (int i = 1; i <= count; i++)
        {
            var frame = await Grab(window);
            if (frame is null) { Console.Error.WriteLine("撮れませんでした"); return 1; }
            var path = count == 1 ? outPath : Numbered(outPath, i);
            frame.SavePng(path);
            Console.WriteLine($"{i}/{count}  {frame.Width}x{frame.Height} → {Path.GetFullPath(path)}");
            if (i < count) await Task.Delay(interval * 1000);
        }
        Console.WriteLine("⚠ この画像は送られません。テンプレートを切り出すために手元に置くだけです");
        Console.WriteLine("⚠ 選手名が写ります。リポジトリに入れないでください");
        return 0;
    }

    private static int Number(string? text, int fallback)
        => int.TryParse(text, out int value) ? value : fallback;

    private static string Numbered(string path, int index)
        => Path.Combine(Path.GetDirectoryName(path) ?? "",
                        $"{Path.GetFileNameWithoutExtension(path)}-{index:D2}{Path.GetExtension(path)}");

    private static async Task<Frame?> Grab(WindowInfo window)
    {
        if (Capture.IsSupported)
        {
            using var capture = new Capture(window.Handle);
            var frame = await capture.GrabAsync(TimeSpan.FromSeconds(5));
            if (frame is not null) return frame;
            Console.WriteLine("WGC で撮れなかったので PrintWindow を試します");
        }
        return PrintWindowCapture.Grab(window.Handle);
    }

    // ---- テンプレートを作る -------------------------------------------------

    private static int Crop(Args a)
    {
        var input = a.Require("--in");
        var outPath = a.Require("--out");
        var rect = Rect.Parse(a.Require("--rect"));
        var frame = Frame.LoadPng(input);
        var piece = frame.Crop(rect);
        piece.SavePng(outPath);
        Console.WriteLine($"{rect} を切り出しました → {Path.GetFullPath(outPath)}（{piece.Width}x{piece.Height}）");
        Console.WriteLine($"⚠ この画像を切り出した画面の高さは {frame.Height} です。"
                        + $"templates/templates.json の referenceHeight をこの値に合わせてください");
        return 0;
    }

    /// <summary>
    /// 枠の中の数字を、字ごとに切り分けて保存する。
    ///
    /// **目分量で桁を切ると隣が混ざる**（実際に 3 枚とも混ざった）。
    /// **字と字のあいだの隙間を見て切る。**
    ///
    /// **高さは枠全体で揃える。** 字ごとに詰めると `1` だけ細くなり、
    /// **テンプレートの大きさが揃わなくなる。**
    /// </summary>
    private static int Slice(Args a)
    {
        var frame = Frame.LoadPng(a.Require("--in"));
        var area = Rect.Parse(a.Require("--rect")).ClampTo(frame.Width, frame.Height);
        var prefix = a.Value("--out") ?? "glyph";
        int threshold = Number(a.Value("--threshold"), 150);
        int margin = Number(a.Value("--margin"), 1);
        var gray = frame.ToGray();

        bool InkColumn(int x)
        {
            for (int y = area.Y; y < area.Bottom; y++) if (gray[x, y] >= threshold) return true;
            return false;
        }
        bool InkRow(int y)
        {
            for (int x = area.X; x < area.Right; x++) if (gray[x, y] >= threshold) return true;
            return false;
        }

        int top = -1, bottom = -1;
        for (int y = area.Y; y < area.Bottom; y++)
            if (InkRow(y)) { if (top < 0) top = y; bottom = y; }
        if (top < 0) { Console.WriteLine("この枠には字がありません（--threshold を下げてみてください）"); return 1; }

        var runs = new List<(int From, int To)>();
        int start = -1;
        for (int x = area.X; x < area.Right; x++)
        {
            if (InkColumn(x)) { if (start < 0) start = x; }
            else if (start >= 0) { runs.Add((start, x - 1)); start = -1; }
        }
        if (start >= 0) runs.Add((start, area.Right - 1));

        Console.WriteLine($"字の高さ: y {top}..{bottom}（{bottom - top + 1} 画素）");
        int n = 0;
        foreach (var (from, to) in runs)
        {
            n++;
            var cut = new Rect(from - margin, top - margin,
                               to - from + 1 + margin * 2, bottom - top + 1 + margin * 2);
            var path = $"{prefix}-{n:D2}.png";
            frame.Crop(cut).SavePng(path);
            Console.WriteLine($"  {n,2}  {cut}  → {path}");
        }
        Console.WriteLine($"{n} 個に切りました。**要るものだけ `digit-N.png` に名前を変えてください**");
        Console.WriteLine("⚠ 桁区切りの `,` も 1 個に数えられます。捨ててください");
        return 0;
    }

    // ---- 校正 ---------------------------------------------------------------

    private static int Match(Args a)
    {
        var c = a.Config();
        var templates = TemplateSet.Load(c.ResolvedTemplatesDir);
        var frame = Frame.LoadPng(a.Require("--in"));
        var reader = new ScreenReader(templates, c);
        var image = reader.Normalize(frame);
        Console.WriteLine($"画像 {frame.Width}x{frame.Height} → 基準の高さ {templates.ReferenceHeight} で {image.Width}x{image.Height}");
        if (templates.Labels.Count == 0) { Console.WriteLine("テンプレートが 1 枚もありません"); return 1; }
        foreach (var (name, template) in templates.Labels.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var hits = Matcher.FindAll(image, template.Image, c.LabelThreshold, 4);
            Console.WriteLine(hits.Count == 0
                ? $"  {name,-22} 当たりません"
                : $"  {name,-22} " + string.Join(" / ", hits.Select(h => $"{h.Where} {h.Score:F3}")));
        }
        return 0;
    }

    private static int Read(Args a)
    {
        var c = a.Config();
        var templates = TemplateSet.Load(c.ResolvedTemplatesDir);
        var missing = templates.Missing().ToList();
        if (missing.Count > 0) Console.WriteLine("足りないテンプレート: " + string.Join(", ", missing));
        var frame = Frame.LoadPng(a.Require("--in"));
        var reader = new ScreenReader(templates, c);
        Console.WriteLine("― ゲームコード ―");
        var code = reader.ReadGameCode(frame, s => Console.WriteLine("  " + s));
        Console.WriteLine("  " + (code?.ToString() ?? "読めません"));
        Console.WriteLine("― 結果 ―");
        var result = reader.ReadResult(frame, s => Console.WriteLine("  " + s));
        Console.WriteLine("  " + (result?.ToString() ?? "読めません"));
        if (result is not null)
            Console.WriteLine("  送るなら: " + Observation.Score(c.BotUserId, c.SenderDiscordId,
                result.HomeGoals, result.AwayGoals, result.Side, c.MatchId));
        return 0;
    }

    // ---- 送る ---------------------------------------------------------------

    private static async Task<int> Send(Args a)
    {
        var c = a.Config();
        string line;
        if (a.Value("--code") is { } code)
            line = Observation.Code(c.BotUserId, c.SenderDiscordId, code, c.MatchId);
        else if (a.Values("--score") is { Count: 2 } score)
            line = Observation.Score(c.BotUserId, c.SenderDiscordId,
                int.Parse(score[0]), int.Parse(score[1]), ParseSide(a.Value("--side")), c.MatchId);
        else return Print("--code <6 桁> か --score <ホーム> <アウェイ> のどちらかが要ります", 2);

        var problems = Observation.Problems(line).Concat(c.DryRun ? [] : c.Problems()).ToList();
        Console.WriteLine("本文: " + line);
        if (problems.Count > 0) return Print("送りません:\n  " + string.Join("\n  ", problems), 1);
        if (c.DryRun) return Print("--dry-run なので送っていません");

        using var webhook = new Webhook(c.WebhookUrl);
        var sent = await webhook.SendAsync(line);
        return sent.Ok ? Print("送りました") : Print("送れませんでした: " + sent.Reason, 1);
    }

    private static Side ParseSide(string? s) => s switch
    {
        "home" => Side.Home,
        "away" => Side.Away,
        _ => Side.Unknown,
    };

    // ---- 常駐 ---------------------------------------------------------------

    private static async Task<int> Watch(Args a)
    {
        var c = a.Config();
        var templates = TemplateSet.Load(c.ResolvedTemplatesDir);
        var missing = templates.Missing().ToList();
        if (missing.Count > 0)
        {
            Console.Error.WriteLine("テンプレートが足りないので読めません: " + string.Join(", ", missing));
            Console.Error.WriteLine("`capture` で 1 枚撮り、`crop` で切り出してください（README を見てください）");
            return 1;
        }
        var problems = c.DryRun ? [] : c.Problems();
        if (problems.Count > 0)
        {
            Console.Error.WriteLine("設定が足りません:\n  " + string.Join("\n  ", problems));
            return 1;
        }
        using var webhook = c.DryRun ? null : new Webhook(c.WebhookUrl);
        var watcher = new Watcher(c, new ScreenReader(templates, c), webhook,
                                  s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}"));
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.WriteLine($"常駐します（{c.PollIntervalMs} ミリ秒ごと / Ctrl+C で終わります）");
        Console.WriteLine("⚠ 送るのは数字だけです。画像は送りません");
        try { await watcher.RunAsync(cts.Token); } catch (OperationCanceledException) { }
        Console.WriteLine("終わりました");
        return 0;
    }

    // ---- 設定 ---------------------------------------------------------------

    private static int Check(Args a)
    {
        var c = a.Config();
        Console.WriteLine($"設定       : {a.ConfigPath}");
        Console.WriteLine($"Webhook    : {Config.Redacted(c.WebhookUrl)}");
        Console.WriteLine($"Bot の id  : {c.BotUserId}");
        Console.WriteLine($"送り主     : {(string.IsNullOrEmpty(c.SenderDiscordId) ? "（未設定）" : c.SenderDiscordId)}");
        Console.WriteLine($"対戦 id    : {c.MatchId?.ToString() ?? "（無し。選手は所属から引かれます）"}");
        Console.WriteLine($"相手       : プロセス名 {c.ProcessName} / タイトル {c.WindowTitle ?? "（指定なし）"}");
        var window = WindowFinder.Find(a.Value("--window") ?? c.ProcessName, c.WindowTitle);
        Console.WriteLine($"見つかった : {(window?.ToString() ?? "（今は起動していません）")}");
        Console.WriteLine($"WGC        : {(Capture.IsSupported ? "使えます" : "使えません")}");

        var templates = TemplateSet.Load(c.ResolvedTemplatesDir);
        Console.WriteLine($"テンプレート: {c.ResolvedTemplatesDir}"
                        + $"（基準の高さ {templates.ReferenceHeight} / ラベル {templates.Labels.Count} 枚 / 数字 {templates.Digits.Count} 枚）");
        var missing = templates.Missing().ToList();
        if (missing.Count > 0) Console.WriteLine("  足りません: " + string.Join(", ", missing));

        // **設定とテンプレートは別々に足りなくなる。** 片方だけ見て「揃った」と言うと、
        // `watch` が黙って何もしないときに、どちらが原因か分からなくなる
        var problems = c.Problems();
        if (problems.Count > 0)
            Console.WriteLine("設定が足りません:\n  " + string.Join("\n  ", problems));
        else if (missing.Count > 0)
            Console.WriteLine("設定は揃っています。ただしテンプレートが無いので、"
                            + "watch は何も読めません（send は送れます）");
        else
            Console.WriteLine("設定もテンプレートも揃っています");
        return problems.Count == 0 && missing.Count == 0 ? 0 : 1;
    }

    private static int ConfigInit(Args a)
    {
        var path = a.ConfigPath;
        if (File.Exists(path)) return Print($"すでにあります: {path}", 1);
        new Config().Save(path);
        Console.WriteLine($"作りました: {path}");
        Console.WriteLine("webhookUrl と senderDiscordId を書いてください。運営なら matchId も要ります");
        return 0;
    }
}

/// <summary>引数。**足りないものは名前を挙げて止まる。**</summary>
public sealed class Args
{
    private readonly string[] _args;
    public Args(string[] args) => _args = args;

    public string? Command => _args.Length > 0 && !_args[0].StartsWith('-') ? _args[0] : null;

    public string? Value(string name)
    {
        int i = Array.IndexOf(_args, name);
        return i >= 0 && i + 1 < _args.Length && !_args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? _args[i + 1] : null;
    }

    public List<string>? Values(string name)
    {
        int i = Array.IndexOf(_args, name);
        if (i < 0) return null;
        var found = new List<string>();
        for (int j = i + 1; j < _args.Length && !_args[j].StartsWith("--", StringComparison.Ordinal); j++)
            found.Add(_args[j]);
        return found;
    }

    public bool Has(string name) => Array.IndexOf(_args, name) >= 0;

    public string Require(string name) => Value(name)
        ?? throw new ArgumentException($"{name} が要ります");

    public string ConfigPath => Value("--config") ?? RematchObserver.Config.DefaultPath;

    private static Config Load(string path) => RematchObserver.Config.Load(path);

    public Config Config()
    {
        var c = Load(ConfigPath);
        if (Has("--dry-run")) c.DryRun = true;
        if (Value("--match") is { } id && int.TryParse(id, out int matchId)) c.MatchId = matchId;
        return c;
    }
}
