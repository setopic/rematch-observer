namespace RematchObserver;

/// <summary>どちら側から見ているか。**運営の観戦では分からない**（縁取りが出ない）。</summary>
public enum Side { Unknown, Home, Away }

public static class SideText
{
    /// <summary>CON-09 の `<側>`。**`-` は「無い」を表す。**</summary>
    public static string Wire(this Side side) => side switch
    {
        Side.Home => "home",
        Side.Away => "away",
        _ => "-",
    };
}

public sealed record ResultReading(int HomeGoals, int AwayGoals, Side Side)
{
    public override string ToString() => $"ホーム {HomeGoals} - アウェイ {AwayGoals}（側: {Side.Wire()}）";
}

public sealed record CodeReading(string Code)
{
    public override string ToString() => $"ゲームコード {Code}";
}

/// <summary>
/// 画面から値を取る。
///
/// **読む場所は表の見出しに頼っている**（CON-09）。
/// `ホーム` / `アウェイ` は文字でラベルされているので、
/// **位置にも色にも視点にも依存しない。**
///
/// ⚠ **`得点` 列を読まない。** 同じ表にあるが 4000 のような別の指標である。
/// 読むのは `ゴール` 列で、**そのラベルを直接探して列の位置を決めている。**
///
/// ⚠ **ヘッダの大きな数字を位置で読まない。**
/// 選手の画面は左が自分のチーム、運営の観戦は左がホームで、**視点で入れ替わる。**
///
/// ⚠ **静かに間違えるくらいなら捨てる**（CON-09）。
/// 曖昧なとき（同じラベルが想定より多く当たったときなど）は null を返す。
/// </summary>
public sealed class ScreenReader
{
    private readonly TemplateSet _t;
    private readonly Config _c;

    public ScreenReader(TemplateSet templates, Config config) { _t = templates; _c = config; }

    /// <summary>
    /// 画面をテンプレートの基準の高さに揃える。
    /// **テンプレートを拡大するのではなく、画面を縮める**（そのほうが崩れない）。
    /// </summary>
    public GrayImage Normalize(Frame frame)
    {
        var gray = frame.ToGray();
        if (gray.Height == _t.ReferenceHeight) return gray;
        int width = (int)Math.Round(gray.Width * (double)_t.ReferenceHeight / gray.Height);
        return gray.Resize(Math.Max(1, width), _t.ReferenceHeight);
    }

    /// <summary>
    /// `ホーム` / `アウェイ` の `合計マッチ数` 行の `ゴール` 列を読む。
    /// **結果の画面でなければ null**（毎周期呼ばれるので例外にしない）。
    /// </summary>
    public ResultReading? ReadResult(Frame frame, Action<string>? trace = null)
    {
        if (!_t.HasLabels || !_t.HasDigits)
        {
            trace?.Invoke("テンプレートが足りません");
            return null;
        }
        var image = Normalize(frame);

        var home = Matcher.FindBest(image, _t.Labels[TemplateSet.Home].Image, _c.LabelThreshold);
        var away = Matcher.FindBest(image, _t.Labels[TemplateSet.Away].Image, _c.LabelThreshold);
        if (home is null || away is null)
        {
            trace?.Invoke($"ホーム/アウェイのラベルが出ていません（home={Fmt(home)} away={Fmt(away)}）");
            return null;
        }
        trace?.Invoke($"ホーム {home.Value.Where} {home.Value.Score:F3} / アウェイ {away.Value.Where} {away.Value.Score:F3}");

        var homeBand = Band(image, home.Value, away.Value);
        var awayBand = Band(image, away.Value, home.Value);

        // `ゴール` は表に 1 つのこともセクションごとに 1 つのこともある。
        // **どちらでも良いが、当たりが増えすぎたら捨てる**（`得点` 列と取り違えない）
        var goals = Matcher.FindAll(image, _t.Labels[TemplateSet.Goals].Image, _c.LabelThreshold, 4);
        if (goals.Count is 0 or > 2)
        {
            trace?.Invoke($"`ゴール` 列が決まりません（当たり {goals.Count} 件）");
            return null;
        }
        trace?.Invoke("ゴール列: " + string.Join(" / ", goals.Select(g => $"{g.Where} {g.Score:F3}")));

        int? h = ReadCell(image, homeBand, goals, "ホーム", trace);
        int? a = ReadCell(image, awayBand, goals, "アウェイ", trace);
        if (h is null || a is null) return null;

        var side = _c.DetectSide ? DetectSide(frame, image, homeBand, awayBand, trace) : Side.Unknown;
        return new ResultReading(h.Value, a.Value, side);
    }

    private static string Fmt(Hit? hit) => hit is null ? "無し" : hit.Value.Score.ToString("F3");

    /// <summary>セクションの帯。**そのラベルから、次のラベルの手前まで。**</summary>
    private static Rect Band(GrayImage image, Hit self, Hit other)
    {
        int top = self.Where.Y;
        int bottom = other.Where.Y > self.Where.Y ? other.Where.Y : image.Height;
        return new Rect(0, top, image.Width, Math.Max(0, bottom - top));
    }

    private int? ReadCell(GrayImage image, Rect band, List<Hit> goalHits, string what, Action<string>? trace)
    {
        var totals = Matcher.FindAll(image, _t.Labels[TemplateSet.TotalMatches].Image,
                                     _c.LabelThreshold, 3, band);
        if (totals.Count != 1)
        {
            trace?.Invoke($"{what}: `合計マッチ数` 行が決まりません（当たり {totals.Count} 件）");
            return null;
        }
        var row = totals[0];

        // 帯の中にある `ゴール` を優先し、無ければ表で 1 つだけのものを使う
        var column = goalHits.FirstOrDefault(g => g.CenterY >= band.Y && g.CenterY < band.Bottom,
                                             goalHits.Count == 1 ? goalHits[0] : default);
        if (column.Where.W == 0)
        {
            trace?.Invoke($"{what}: `ゴール` 列がこのセクションに結び付きません");
            return null;
        }

        var cell = new Rect(
            column.CenterX - (int)(column.Where.W * _c.CellWidthScale / 2),
            row.CenterY - (int)(row.Where.H * _c.CellHeightScale / 2),
            Math.Max(1, (int)(column.Where.W * _c.CellWidthScale)),
            Math.Max(1, (int)(row.Where.H * _c.CellHeightScale)));

        var text = DigitReader.Read(image, cell, _t, _c.DigitThreshold, 3, _c.InkThreshold);
        trace?.Invoke($"{what}: 枠 {cell} → {(text ?? "読めません")}");
        if (text is null || !int.TryParse(text, out int value)) return null;
        return value;
    }

    /// <summary>
    /// チーム選択画面の `ゲームコード:`。**6 桁の数字**（CON-09）。
    /// **6 桁ちょうどでなければ捨てる。**
    /// </summary>
    public CodeReading? ReadGameCode(Frame frame, Action<string>? trace = null)
    {
        if (!_t.Labels.TryGetValue(TemplateSet.GameCode, out var label) || !_t.HasDigits)
        {
            trace?.Invoke("ゲームコードのテンプレートが足りません");
            return null;
        }
        var image = Normalize(frame);
        var hit = Matcher.FindBest(image, label.Image, _c.LabelThreshold);
        if (hit is null) { trace?.Invoke("`ゲームコード` のラベルが出ていません"); return null; }

        var band = new Rect(
            hit.Value.Where.Right,
            hit.Value.Where.Y - (int)(hit.Value.Where.H * 0.3),
            Math.Max(1, (int)(hit.Value.Where.H * _c.CodeWidthScale)),
            Math.Max(1, (int)(hit.Value.Where.H * 1.6)));

        var text = DigitReader.Read(image, band, _t, _c.DigitThreshold, 8, _c.InkThreshold);
        trace?.Invoke($"ゲームコード: ラベル {hit.Value.Where} {hit.Value.Score:F3} 枠 {band} → {text ?? "読めません"}");
        if (text is null || text.Length != 6) return null;
        return new CodeReading(text);
    }

    /// <summary>
    /// 黄色の縁取りから自分の側を出す。**既定では呼ばれない**（config の detectSide）。
    ///
    /// ⚠ **緑の背景を使わない。** あれはゲーム内で選択している選手に付くもので、
    /// **運営の観戦では他人の行に付く。** 使ってよいのは黄色の縁取りだけである。
    /// ⚠ **運営の観戦では縁取りが出ない。** そのときは Unknown のままにする。
    /// ⚠ **側を間違えて送ると Bot が観測ごと捨てる**（UC-45 A3）。**迷ったら Unknown。**
    /// </summary>
    private Side DetectSide(Frame frame, GrayImage normalized, Rect homeBand, Rect awayBand,
                            Action<string>? trace)
    {
        double scale = frame.Height / (double)normalized.Height;
        int home = CountOutline(frame, Scale(homeBand, scale, frame));
        int away = CountOutline(frame, Scale(awayBand, scale, frame));
        trace?.Invoke($"黄色の縁取り: ホーム側 {home} 画素 / アウェイ側 {away} 画素");
        int floor = Math.Max(_c.OutlineMinPixels, 1);
        if (home < floor && away < floor) return Side.Unknown;         // 運営の観戦
        if (home >= floor && away >= floor) return Side.Unknown;       // 両側に出るのはおかしい
        return home >= floor ? Side.Home : Side.Away;
    }

    private static Rect Scale(Rect r, double s, Frame frame) => new Rect(
        (int)(r.X * s), (int)(r.Y * s), (int)(r.W * s), (int)(r.H * s)).ClampTo(frame.Width, frame.Height);

    private int CountOutline(Frame frame, Rect area)
    {
        int n = 0;
        for (int y = area.Y; y < area.Bottom; y++)
            for (int x = area.X; x < area.Right; x++)
            {
                var (b, g, r, _) = frame.At(x, y);
                if (r >= _c.OutlineMinRed && g >= _c.OutlineMinGreen && b <= _c.OutlineMaxBlue
                    && r - b >= _c.OutlineMinSpread && g - b >= _c.OutlineMinSpread) n++;
            }
        return n;
    }
}

/// <summary>
/// 枠の中の数字を読む。
///
/// **OCR ライブラリを使わない。** ゲームの UI はフォントが固定なので、
/// **数字も画像で照合できる**（ADR-0067）。
/// **読めなければ null。** 静かに間違えるより捨てるほうが良い（CON-09）。
/// </summary>
public static class DigitReader
{
    public static string? Read(GrayImage image, Rect where, TemplateSet templates,
                               double threshold, int maxDigits, int inkThreshold = 150)
    {
        var area = where.ClampTo(image.Width, image.Height);
        if (area.W <= 0 || area.H <= 0) return null;

        var found = new List<(int X, char Digit, double Score)>();
        foreach (var (digit, template) in templates.Digits)
            foreach (var hit in Matcher.FindAll(image, template.Image, threshold, maxDigits, area))
                found.Add((hit.Where.X, digit, hit.Score));
        if (found.Count == 0) return null;

        // **良い順に取り、重なったものは捨てる。** 同じ場所を 2 つの数字が名乗ることがある
        found.Sort((a, b) => b.Score.CompareTo(a.Score));
        var taken = new List<(int X, int W, char Digit)>();
        foreach (var f in found)
        {
            int w = templates.Digits[f.Digit].Image.Width;
            if (taken.Any(t => Math.Min(t.X + t.W, f.X + w) - Math.Max(t.X, f.X) > w / 2)) continue;
            taken.Add((f.X, w, f.Digit));
            if (taken.Count >= maxDigits) break;
        }
        if (taken.Count == 0) return null;

        // ⚠ **字があるのに当たらなかった所が残っていたら捨てる。**
        //
        // これが無いと、**読めなかった桁が黙って落ちる。**
        // `4` のテンプレートが無いときに `14` を `1` と読むことになり、
        // **読めないのではなく間違った値が記録される**（CON-09 が最も避けたい形）。
        //
        // **実際に起きうる。** `4` は合計マッチ数の行に出ないことがあり、
        // そのときテンプレートを作れない（2026-09-08 に実画面で確認）。
        // ⚠ **塊ではなく 1 列ずつ見る。** 隣り合う数字はくっついて 1 つの塊になるので、
        // 「塊のどこかに当たっていればよい」では `14` の `4` を見逃す（試験で踏んだ）。
        const int slack = 2;                        // 縁のぼやけぶんだけ緩める
        for (int x = area.X; x < area.Right; x++)
        {
            bool ink = false;
            for (int y = area.Y; y < area.Bottom && !ink; y++) ink = image[x, y] >= inkThreshold;
            if (!ink) continue;
            if (!taken.Any(t => x >= t.X - slack && x < t.X + t.W + slack)) return null;
        }

        taken.Sort((a, b) => a.X.CompareTo(b.X));
        return new string(taken.Select(t => t.Digit).ToArray());
    }
}
