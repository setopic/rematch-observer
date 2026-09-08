using System.Runtime.Versioning;
using RematchObserver;
using Xunit;

namespace RematchObserver.Tests;

/// <summary>
/// 画面から値を取るところ。**合成した表で通しを見る。**
///
/// ゲームが動いていなくても、**ラベルを探す → 行と列を決める → 数字を読む**
/// という筋道はここで確かめられる。
/// **本物のテンプレートに差し替えたときに壊れるのは、テンプレートであってここではない。**
/// </summary>
[SupportedOSPlatform("windows")]
public class ReadingTests
{
    private static Config Tuned() => new()
    {
        LabelThreshold = 0.80,
        DigitThreshold = 0.85,
        CellWidthScale = 1.6,
        CellHeightScale = 1.6,
        CodeWidthScale = 8.0,
    };

    [Fact]
    public void ホームとアウェイのゴール数を絶対値で読む()
    {
        var built = SyntheticScreen.Build(homeGoals: 4, awayGoals: 0);
        var reader = new ScreenReader(built.Templates, Tuned());
        var result = reader.ReadResult(built.Frame);
        Assert.NotNull(result);
        Assert.Equal(4, result!.HomeGoals);
        Assert.Equal(0, result.AwayGoals);
    }

    [Fact]
    public void 得点の列を読まない()
    {
        // ⚠ **同じ表に 4000 のような別の指標が入っている。**
        // 列名がドメインの用語と一致しているのに意味が違う（CON-09）
        var built = SyntheticScreen.Build(homeGoals: 2, awayGoals: 3, homeDecoy: 4000, awayDecoy: 1200);
        var result = new ScreenReader(built.Templates, Tuned()).ReadResult(built.Frame);
        Assert.NotNull(result);
        Assert.Equal(2, result!.HomeGoals);
        Assert.Equal(3, result.AwayGoals);
    }

    [Fact]
    public void 二桁のゴール数も読む()
    {
        var built = SyntheticScreen.Build(homeGoals: 12, awayGoals: 7);
        var result = new ScreenReader(built.Templates, Tuned()).ReadResult(built.Frame);
        Assert.NotNull(result);
        Assert.Equal(12, result!.HomeGoals);
        Assert.Equal(7, result.AwayGoals);
    }

    [Fact]
    public void 画面の大きさが変わっても読む()
    {
        // **テンプレートは基準の高さで作ってある。** 画面のほうを合わせる
        var built = SyntheticScreen.Build(homeGoals: 3, awayGoals: 1);
        var bigger = Upscale(built.Frame, 1.5);
        var result = new ScreenReader(built.Templates, Tuned()).ReadResult(bigger);
        Assert.NotNull(result);
        Assert.Equal(3, result!.HomeGoals);
        Assert.Equal(1, result.AwayGoals);
    }

    [Fact]
    public void オウンゴールはヘッダから拾う()
    {
        // ⚠ **`合計マッチ数` にオウンゴールが入らない**（CON-09。実画面で確認）。
        // ホームは 1 点入れているのに、選手ゴールの合計は 0 である。
        // **表だけを読むと 0-2 と報告してしまう。**
        var built = SyntheticScreen.Build(homeGoals: 0, awayGoals: 2, homeScore: 1, awayScore: 2);
        var result = new ScreenReader(built.Templates, Tuned()).ReadResult(built.Frame);
        Assert.NotNull(result);
        Assert.Equal(1, result!.HomeGoals);
        Assert.Equal(2, result.AwayGoals);
    }

    [Fact]
    public void ヘッダの左右が入れ替わっても同じ答えになる()
    {
        // **並びは視点で入れ替わる**（CON-09）。**位置では決めない。**
        // 決め手は「どのチームの得点も、選手ゴールの合計を下回らない」
        var normal = SyntheticScreen.Build(homeGoals: 0, awayGoals: 2, homeScore: 1, awayScore: 2);
        var swapped = SyntheticScreen.Build(homeGoals: 0, awayGoals: 2, homeScore: 1, awayScore: 2,
                                            headerSwapped: true);
        var a = new ScreenReader(normal.Templates, Tuned()).ReadResult(normal.Frame);
        var b = new ScreenReader(swapped.Templates, Tuned()).ReadResult(swapped.Frame);
        Assert.Equal("ホーム 1 - アウェイ 2（側: -）", a!.ToString());
        Assert.Equal(a.ToString(), b!.ToString());
    }

    [Fact]
    public void 向きが決まらなければ捨てる()
    {
        // 選手ゴールが 0-0 だと、ヘッダの 1-2 はどちらにも割り当てられる。
        // ⚠ **静かに間違えるくらいなら捨てる**
        var built = SyntheticScreen.Build(homeGoals: 0, awayGoals: 0, homeScore: 1, awayScore: 2);
        Assert.Null(new ScreenReader(built.Templates, Tuned()).ReadResult(built.Frame));
    }

    [Fact]
    public void ヘッダと選手ゴールが矛盾すれば捨てる()
    {
        // 選手ゴールのほうが得点より多い、という画面は読み違えている
        var built = SyntheticScreen.Build(homeGoals: 3, awayGoals: 3, homeScore: 1, awayScore: 2);
        Assert.Null(new ScreenReader(built.Templates, Tuned()).ReadResult(built.Frame));
    }

    [Fact]
    public void ヘッダが読めなければ_表だけで送らない()
    {
        // ⚠ **表だけで送ると、オウンゴールのぶんが落ちたまま記録される**（CON-09）
        var built = SyntheticScreen.Build(homeGoals: 2, awayGoals: 1, withHeader: false);
        Assert.Null(new ScreenReader(built.Templates, Tuned()).ReadResult(built.Frame));
    }

    [Fact]
    public void 結果の画面でなければ_読めないと言う()
    {
        // **静かに間違えるのではなく、読めなければ捨てる**（CON-09）
        var built = SyntheticScreen.Build(homeGoals: 1, awayGoals: 1);
        var blank = new Frame(400, 300, 400 * 4, new byte[400 * 300 * 4]);
        Assert.Null(new ScreenReader(built.Templates, Tuned()).ReadResult(blank));
    }

    [Fact]
    public void ゲームコードは_6桁のときだけ読む()
    {
        var ok = SyntheticScreen.Build(1, 1, gameCode: "330787");
        Assert.Equal("330787", new ScreenReader(ok.Templates, Tuned()).ReadGameCode(ok.Frame)?.Code);

        var tooShort = SyntheticScreen.Build(1, 1, gameCode: "3307");
        Assert.Null(new ScreenReader(tooShort.Templates, Tuned()).ReadGameCode(tooShort.Frame));
    }

    [Fact]
    public void 側は既定で読まない()
    {
        // ⚠ **側を間違えて送ると Bot が観測ごと捨てる**（UC-45 A3）。
        // **校正できていないうちは `-` で送る**
        var built = SyntheticScreen.Build(homeGoals: 1, awayGoals: 0);
        var result = new ScreenReader(built.Templates, Tuned()).ReadResult(built.Frame);
        Assert.Equal(Side.Unknown, result!.Side);
        Assert.Equal("-", result.Side.Wire());
    }

    [Fact]
    public void 読めない桁が混じったら_短い数として読まない()
    {
        // ⚠ **これが最も危ない壊れ方である。**
        // `4` のテンプレートが無いとき、`14` を `1` と読んで**そのまま記録されうる。**
        // **読めないのではなく、間違った値が入る。**
        //
        // **実際に起きうる。** `4` は合計マッチ数の行に出ないことがあり、
        // そのときテンプレートを作れない（2026-09-08 に実画面で確認）。
        var built = SyntheticScreen.Build(homeGoals: 14, awayGoals: 0);
        var withoutFour = new ScreenReader(built.Templates, Tuned());
        Assert.Equal(14, withoutFour.ReadResult(built.Frame)!.HomeGoals);   // 揃っていれば読める

        // **枚数は 10 のままにする。** 減らすと「足りない」で弾かれてしまい、
        // **確かめたい経路を通らない。** 当たらないテンプレートに差し替える
        built.Templates.Digits['4'] = new Template("digit-4", new GrayImage(12, 18, new byte[12 * 18]));
        Assert.True(built.Templates.HasDigits);
        Assert.Null(new ScreenReader(built.Templates, Tuned()).ReadResult(built.Frame));
    }

    [Fact]
    public void 数字のテンプレートが無ければ読まない()
    {
        var built = SyntheticScreen.Build(homeGoals: 1, awayGoals: 0);
        built.Templates.Digits.Clear();
        Assert.Null(new ScreenReader(built.Templates, Tuned()).ReadResult(built.Frame));
    }

    private static Frame Upscale(Frame frame, double factor)
    {
        int w = (int)(frame.Width * factor), h = (int)(frame.Height * factor);
        int stride = w * 4;
        var buf = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Min(frame.Height - 1, (int)(y / factor));
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min(frame.Width - 1, (int)(x / factor));
                Array.Copy(frame.Bgra, sy * frame.Stride + sx * 4, buf, y * stride + x * 4, 4);
            }
        }
        return new Frame(w, h, stride, buf);
    }
}

public class MatcherTests
{
    [Fact]
    public void 同じ模様は_置いた場所で見つかる()
    {
        var image = Noise(400, 300, seed: 7);
        var mark = Stamp(image, atX: 232, atY: 124);
        var hit = Matcher.FindBest(image, mark, 0.9);
        Assert.NotNull(hit);
        Assert.Equal(232, hit!.Value.Where.X);
        Assert.Equal(124, hit.Value.Where.Y);
    }

    [Fact]
    public void 無いものは見つからない()
    {
        var image = Noise(200, 200, seed: 1);
        var other = Noise(20, 20, seed: 99);
        Assert.Null(Matcher.FindBest(image, other, 0.9));
    }

    [Fact]
    public void 同じ模様が二つあれば二つ返す()
    {
        var image = Noise(400, 300, seed: 3);
        var mark = Stamp(image, atX: 60, atY: 40);
        Paste(image, mark, 250, 200);
        var hits = Matcher.FindAll(image, mark, 0.9, 4);
        Assert.Equal(2, hits.Count);
    }

    /// <summary>
    /// 4 画素の塊で作った模様。
    /// ⚠ **1 画素ごとの乱数にしない。** 粗い面は面積の平均で縮めるので、
    /// **1 画素ごとの乱数は縮めると一様な灰色になり、どんな照合器でも当たらない。**
    /// 本物のテンプレートは文字なので、**塊のほうが実物に近い。**
    /// </summary>
    private static GrayImage Noise(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var px = new byte[w * h];
        for (int y = 0; y < h; y += 4)
            for (int x = 0; x < w; x += 4)
            {
                byte v = (byte)rng.Next(256);
                for (int j = y; j < Math.Min(h, y + 4); j++)
                    for (int i = x; i < Math.Min(w, x + 4); i++) px[j * w + i] = v;
            }
        return new GrayImage(w, h, px);
    }

    private static GrayImage Stamp(GrayImage image, int atX, int atY)
    {
        var mark = Noise(24, 18, seed: atX * 31 + atY);
        Paste(image, mark, atX, atY);
        return mark;
    }

    private static void Paste(GrayImage image, GrayImage mark, int x, int y)
    {
        for (int j = 0; j < mark.Height; j++)
            Array.Copy(mark.Pixels, j * mark.Width, image.Pixels, (y + j) * image.Width + x, mark.Width);
    }
}
