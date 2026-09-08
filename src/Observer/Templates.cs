using System.Runtime.Versioning;
using System.Text.Json;

namespace RematchObserver;

/// <summary>照合で当たった 1 か所。</summary>
public readonly record struct Hit(Rect Where, double Score)
{
    public int CenterX => Where.X + Where.W / 2;
    public int CenterY => Where.Y + Where.H / 2;
}

/// <summary>照合の元になる 1 枚。**輝度だけを持つ**（色は見ない）。</summary>
public sealed class Template
{
    public string Name { get; }
    public GrayImage Image { get; }
    public Template(string name, GrayImage image) { Name = name; Image = image; }
}

/// <summary>
/// templates/ の中身。
///
/// **ラベルは画像で照合し、数字だけ読む**（ADR-0067）。
/// **日本語の OCR 言語パックが要らない**のはそのためで、
/// **ゲームの UI はフォントが固定なので、数字も画像照合で足りる。**
///
/// ⚠ **ゲームの UI が変わったときに直すのは、主にここであってコードではない。**
/// </summary>
public sealed class TemplateSet
{
    public const string Home = "label-home";
    public const string Away = "label-away";
    public const string TotalMatches = "label-total-matches";
    public const string Goals = "label-goals";
    public const string GameCode = "label-game-code";

    /// <summary>テンプレートを切り出した画面の高さ。**画面のほうをこの高さに合わせる。**</summary>
    public int ReferenceHeight { get; init; } = 1080;

    public Dictionary<string, Template> Labels { get; } = new(StringComparer.Ordinal);

    /// <summary>リザルトの表の `合計マッチ数` の行の数字（18 画素の太字）。</summary>
    public Dictionary<char, Template> Digits { get; } = new();

    /// <summary>
    /// ヘッダの得点の数字（15 画素）。**選手の行と同じ大きさ・字体である**
    /// （2026-09-08 に実画面で確認）ので、選手の行から取ってよい。
    /// 空なら <see cref="Digits"/> を縮めて代用する。
    /// </summary>
    public Dictionary<char, Template> HeaderDigits { get; } = new();

    /// <summary>
    /// ゲームコードの数字。**表とは別の字体である**（細身で、大きさも違う）。
    /// ⚠ **縮めても橋渡しできない**（2026-09-08 に実画面で確認）。
    ///
    /// **揃っていなくてよい。** 足りない数字を含むコードは、
    /// 読めないものとして捨てられるだけである（誤った値は入らない）。
    /// 空なら <see cref="Digits"/> で代用する。
    /// </summary>
    public Dictionary<char, Template> CodeDigits { get; } = new();

    public bool HasLabels => Labels.ContainsKey(Home) && Labels.ContainsKey(Away)
                          && Labels.ContainsKey(TotalMatches) && Labels.ContainsKey(Goals);
    public bool HasDigits => Digits.Count == 10;

    private sealed class Manifest
    {
        public int ReferenceHeight { get; set; } = 1080;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// フォルダから読む。**足りなくても落ちない。**
    /// 何が足りないかは <see cref="Missing"/> が言う。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static TemplateSet Load(string dir)
    {
        int reference = 1080;
        var manifestPath = Path.Combine(dir, "templates.json");
        if (File.Exists(manifestPath))
        {
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), Json);
            if (m is not null && m.ReferenceHeight > 0) reference = m.ReferenceHeight;
        }
        var set = new TemplateSet { ReferenceHeight = reference };
        if (!Directory.Exists(dir)) return set;
        foreach (var path in Directory.EnumerateFiles(dir, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var gray = Frame.LoadPng(path).ToGray();
            if (name.StartsWith("header-digit-", StringComparison.Ordinal) && name.Length == 14
                && char.IsAsciiDigit(name[13]))
                set.HeaderDigits[name[13]] = new Template(name, gray);
            else if (name.StartsWith("code-digit-", StringComparison.Ordinal) && name.Length == 12
                && char.IsAsciiDigit(name[11]))
                set.CodeDigits[name[11]] = new Template(name, gray);
            else if (name.StartsWith("digit-", StringComparison.Ordinal) && name.Length == 7
                     && char.IsAsciiDigit(name[6]))
                set.Digits[name[6]] = new Template(name, gray);
            else
                set.Labels[name] = new Template(name, gray);
        }
        return set;
    }

    public static void SaveManifest(string dir, int referenceHeight)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "templates.json"),
            JsonSerializer.Serialize(new Manifest { ReferenceHeight = referenceHeight }, Json));
    }

    public IEnumerable<string> Missing()
    {
        foreach (var n in new[] { Home, Away, TotalMatches, Goals })
            if (!Labels.ContainsKey(n)) yield return n + ".png";
        if (!Labels.ContainsKey(GameCode)) yield return GameCode + ".png（ゲームコードを読むなら要る）";
        for (char c = '0'; c <= '9'; c++)
            if (!Digits.ContainsKey(c)) yield return $"digit-{c}.png";
    }
}

/// <summary>
/// 正規化相互相関で探す。
///
/// **明るさの違いに強い**ので、画面の設定や色の補正で崩れにくい。
/// **粗く探してから細かく詰める**（4 分の 1 に縮めた面で当たりを付け、
/// その周りだけを原寸で見る）。全面を原寸で見ると 1 枚に数秒かかる。
/// </summary>
public static class Matcher
{
    private const int Coarse = 4;

    /// <summary>
    /// <paramref name="template"/> が入っている場所を、良い順に返す。
    /// **重なった当たりはまとめる**（同じラベルを 2 回数えない）。
    /// </summary>
    public static List<Hit> FindAll(GrayImage image, GrayImage template, double threshold,
                                    int maxResults = 8, Rect? within = null)
    {
        var area = (within ?? new Rect(0, 0, image.Width, image.Height)).ClampTo(image.Width, image.Height);
        if (area.W < template.Width || area.H < template.Height) return new List<Hit>();

        var region = image.Crop(area);
        var hits = new List<Hit>();

        var smallImage = region.Resize(Math.Max(1, region.Width / Coarse), Math.Max(1, region.Height / Coarse));
        var smallTemplate = template.Resize(Math.Max(1, template.Width / Coarse), Math.Max(1, template.Height / Coarse));
        if (smallImage.Width < smallTemplate.Width || smallImage.Height < smallTemplate.Height
            || smallTemplate.Width < 3 || smallTemplate.Height < 3)
            return Exhaustive(region, template, threshold, maxResults, area);

        // **粗い面では閾値で切らない。**
        // 縮めるときの升目が、テンプレートと画像とで揃うとは限らない
        // （テンプレートが 4 の倍数の位置にあるとは限らない）ので、
        // **本物の当たりでも粗い面のスコアは落ちる。**
        // だから「良さそうな山」を多めに拾い、**原寸で詰めてから閾値を当てる。**
        var candidates = Peaks(smallImage, smallTemplate, Math.Max(12, maxResults * 4));

        var taken = new List<Rect>();
        foreach (var c in candidates)
        {
            if (hits.Count >= maxResults) break;
            var best = Refine(region, template, c.Where.X * Coarse, c.Where.Y * Coarse, Coarse + 2);
            if (best.Score < threshold) continue;
            var placed = new Rect(best.Where.X + area.X, best.Where.Y + area.Y, template.Width, template.Height);
            if (taken.Any(t => Overlaps(t, placed))) continue;
            taken.Add(placed);
            hits.Add(new Hit(placed, best.Score));
        }
        hits.Sort((a, b) => b.Score.CompareTo(a.Score));
        return hits;
    }

    /// <summary>粗い面のスコアの山を、良い順に <paramref name="k"/> 個まで。</summary>
    private static List<Hit> Peaks(GrayImage image, GrayImage template, int k)
    {
        int w = image.Width - template.Width + 1, h = image.Height - template.Height + 1;
        var stats = Stats(template);
        if (stats.Norm <= 0 || w <= 0 || h <= 0) return new List<Hit>();

        var scores = new double[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                scores[y * w + x] = Correlate(image, template, x, y, stats);

        var peaks = new List<Hit>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double v = scores[y * w + x];
                if (v <= 0) continue;
                bool top = true;
                for (int dy = -1; dy <= 1 && top; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h || (dx == 0 && dy == 0)) continue;
                        if (scores[ny * w + nx] > v) { top = false; break; }
                    }
                if (top) peaks.Add(new Hit(new Rect(x, y, template.Width, template.Height), v));
            }
        peaks.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (peaks.Count > k) peaks.RemoveRange(k, peaks.Count - k);
        return peaks;
    }

    public static Hit? FindBest(GrayImage image, GrayImage template, double threshold, Rect? within = null)
    {
        var all = FindAll(image, template, threshold, 1, within);
        return all.Count == 0 ? null : all[0];
    }

    private static List<Hit> Exhaustive(GrayImage region, GrayImage template, double threshold,
                                        int maxResults, Rect area)
    {
        var found = Scan(region, template, threshold);
        found.Sort((a, b) => b.Score.CompareTo(a.Score));
        var taken = new List<Rect>();
        var hits = new List<Hit>();
        foreach (var f in found)
        {
            if (hits.Count >= maxResults) break;
            var placed = new Rect(f.Where.X + area.X, f.Where.Y + area.Y, template.Width, template.Height);
            if (taken.Any(t => Overlaps(t, placed))) continue;
            taken.Add(placed);
            hits.Add(new Hit(placed, f.Score));
        }
        return hits;
    }

    private static bool Overlaps(Rect a, Rect b)
    {
        int w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        int h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        if (w <= 0 || h <= 0) return false;
        // 半分以上重なっていれば同じものとみなす
        return (long)w * h * 2 >= (long)Math.Min(a.W * a.H, b.W * b.H);
    }

    private static Hit Refine(GrayImage image, GrayImage template, int guessX, int guessY, int radius)
    {
        double best = -1; int bx = guessX, by = guessY;
        int x0 = Math.Max(0, guessX - radius), x1 = Math.Min(image.Width - template.Width, guessX + radius);
        int y0 = Math.Max(0, guessY - radius), y1 = Math.Min(image.Height - template.Height, guessY + radius);
        var stats = Stats(template);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double s = Correlate(image, template, x, y, stats);
                if (s > best) { best = s; bx = x; by = y; }
            }
        return new Hit(new Rect(bx, by, template.Width, template.Height), best);
    }

    private static List<Hit> Scan(GrayImage image, GrayImage template, double threshold)
    {
        var hits = new List<Hit>();
        var stats = Stats(template);
        if (stats.Norm <= 0) return hits;          // 一色のテンプレートは照合できない
        for (int y = 0; y + template.Height <= image.Height; y++)
            for (int x = 0; x + template.Width <= image.Width; x++)
            {
                double s = Correlate(image, template, x, y, stats);
                if (s >= threshold) hits.Add(new Hit(new Rect(x, y, template.Width, template.Height), s));
            }
        return hits;
    }

    private readonly record struct TemplateStats(double Norm, double[] Centered);

    private static TemplateStats Stats(GrayImage t)
    {
        int n = t.Width * t.Height;
        long sum = 0;
        for (int i = 0; i < n; i++) sum += t.Pixels[i];
        double mean = sum / (double)n;
        var centered = new double[n];
        double sq = 0;
        for (int i = 0; i < n; i++)
        {
            centered[i] = t.Pixels[i] - mean;
            sq += centered[i] * centered[i];
        }
        return new TemplateStats(Math.Sqrt(sq), centered);
    }

    private static double Correlate(GrayImage image, GrayImage t, int ox, int oy, TemplateStats s)
    {
        int n = t.Width * t.Height;
        long sum = 0, sumSq = 0;
        double cross = 0;
        for (int y = 0; y < t.Height; y++)
        {
            int src = (oy + y) * image.Width + ox;
            int ti = y * t.Width;
            for (int x = 0; x < t.Width; x++)
            {
                int v = image.Pixels[src + x];
                sum += v; sumSq += (long)v * v;
                cross += v * s.Centered[ti + x];
            }
        }
        double variance = sumSq - (double)sum * sum / n;
        if (variance <= 1e-9 || s.Norm <= 1e-9) return 0;
        return cross / (Math.Sqrt(variance) * s.Norm);
    }
}
