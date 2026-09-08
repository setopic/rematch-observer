using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.Versioning;
using RematchObserver;

namespace RematchObserver.Tests;

/// <summary>
/// 本物の画面の代わりに、同じ形の表を描く。
///
/// **ゲームが動いていなくても、読み取りの筋道を通しで確かめられる。**
/// 文字は英字にしてある。**照合しているのは字形であって意味ではない**ので、
/// これで確かめられるのは本物と同じ仕組みである
/// （日本語のフォントが入っていない CI でも動く、という利点もある）。
///
/// ⚠ **`得点` にあたる列をわざと隣に置いてある。**
/// **そちらを読んでいないこと**を確かめるためで、これが目的の半分である。
/// </summary>
[SupportedOSPlatform("windows")]
public static class SyntheticScreen
{
    public const string HomeLabel = "HOME";
    public const string AwayLabel = "AWAY";
    public const string TotalLabel = "TOTAL";
    public const string GoalsLabel = "GOALS";
    public const string DecoyLabel = "POINTS";      // `得点` の位置にいる別物
    public const string CodeLabel = "GAMECODE";

    private const int Width = 1600, Height = 1080;
    private const int GoalsColumnX = 820, DecoyColumnX = 1180;

    public sealed record Built(Frame Frame, TemplateSet Templates);

    public static Built Build(int homeGoals, int awayGoals,
                              int homeDecoy = 4000, int awayDecoy = 1200, string? gameCode = null)
    {
        var rects = new Dictionary<string, Rect>(StringComparer.Ordinal);
        var digitRects = new Dictionary<char, Rect>();

        using var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            g.Clear(Color.FromArgb(255, 38, 40, 46));
            using var font = new Font(FontFamily.GenericSansSerif, 22, FontStyle.Bold, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.White);

            Section(g, font, white, rects, "home", top: 180, goals: homeGoals, decoy: homeDecoy);
            Section(g, font, white, rects, "away", top: 560, goals: awayGoals, decoy: awayDecoy);

            if (gameCode is not null)
            {
                // ⚠ **コードはラベルの「下の行」に描く。** 実画面がそうなっている
                // （2026-09-08 に確認）。右に描くと、実物と違う形を試験してしまう
                rects[CodeLabel] = Left(g, font, white, CodeLabel, 80, 860);
                Left(g, font, white, gameCode, 80, rects[CodeLabel].Bottom + 6);
            }

            // **数字のテンプレートは同じ描き方で作る。** 別の描き方だと照合できない
            int x = 100;
            for (char d = '0'; d <= '9'; d++)
            {
                digitRects[d] = Left(g, font, white, d.ToString(), x, 980);
                x += 60;
            }
        }

        var frame = ToFrame(bmp);
        var gray = frame.ToGray();
        var templates = new TemplateSet { ReferenceHeight = Height };
        Add(templates, TemplateSet.Home, gray, rects[HomeLabel]);
        Add(templates, TemplateSet.Away, gray, rects[AwayLabel]);
        Add(templates, TemplateSet.TotalMatches, gray, rects[TotalLabel]);
        Add(templates, TemplateSet.Goals, gray, rects[GoalsLabel]);
        if (rects.TryGetValue(CodeLabel, out var codeRect))
            Add(templates, TemplateSet.GameCode, gray, codeRect);
        foreach (var (d, r) in digitRects)
            templates.Digits[d] = new Template($"digit-{d}", gray.Crop(r));

        return new Built(frame, templates);
    }

    /// <summary>`得点` にあたる列のテンプレート。**読んではいけないほうを名指しで持てるようにする。**</summary>
    [SupportedOSPlatform("windows")]
    public static GrayImage DecoyTemplate(Built built)
    {
        // 描いた位置から引き直す。列見出しの中心はレイアウトの定数で決まっている
        var gray = built.Frame.ToGray();
        return gray.Crop(new Rect(DecoyColumnX - 60, 250 - 2, 120, 34));
    }

    private static void Section(Graphics g, Font font, Brush brush, Dictionary<string, Rect> rects,
                                string which, int top, int goals, int decoy)
    {
        string label = which == "home" ? HomeLabel : AwayLabel;
        rects[label] = Left(g, font, brush, label, 80, top);

        int headerY = top + 70, rowY = top + 140;
        var goalsHeader = Centered(g, font, brush, GoalsLabel, GoalsColumnX, headerY);
        Centered(g, font, brush, DecoyLabel, DecoyColumnX, headerY);
        var total = Left(g, font, brush, TotalLabel, 100, rowY);
        Centered(g, font, brush, goals.ToString(), GoalsColumnX, rowY);
        Centered(g, font, brush, decoy.ToString(), DecoyColumnX, rowY);

        // テンプレートに使うのは片方のセクションのぶんだけでよい（同じ字形なので）
        if (which == "home") { rects[GoalsLabel] = goalsHeader; rects[TotalLabel] = total; }
    }

    private static Rect Left(Graphics g, Font font, Brush brush, string text, int x, int y)
    {
        g.DrawString(text, font, brush, x, y);
        var size = g.MeasureString(text, font);
        return Tighten(new Rect(x, y, (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)));
    }

    private static Rect Centered(Graphics g, Font font, Brush brush, string text, int centerX, int y)
    {
        var size = g.MeasureString(text, font);
        int x = centerX - (int)(size.Width / 2);
        g.DrawString(text, font, brush, x, y);
        return Tighten(new Rect(x, y, (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)));
    }

    /// <summary>MeasureString は余白を多めに返す。**余白ごと切ると照合が鈍る。**</summary>
    private static Rect Tighten(Rect r)
        => new(r.X + 2, r.Y + 2, Math.Max(1, r.W - 5), Math.Max(1, r.H - 6));

    private static void Add(TemplateSet set, string name, GrayImage gray, Rect where)
        => set.Labels[name] = new Template(name, gray.Crop(where));

    private static Frame ToFrame(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = bmp.Width * 4;
            var buf = new byte[stride * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, buf, y * stride, stride);
            return new Frame(bmp.Width, bmp.Height, stride, buf);
        }
        finally { bmp.UnlockBits(data); }
    }
}
