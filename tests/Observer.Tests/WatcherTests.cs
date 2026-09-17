using System.Runtime.Versioning;
using RematchObserver;
using Xunit;

namespace RematchObserver.Tests;

/// <summary>
/// 送る / 送らないの判断。**ここが唯一の事前の防御である。**
///
/// ⚠ **完全自動なので誰も数字を見ない**（ADR-0067）。
/// `/confirm_all` は由来で分けないので、**入った誤りは確定では止まらない。**
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public class WatcherTests
{
    private static (Watcher Watcher, List<string> Log) Make(TemplateSet templates, int stableReads = 2,
                                                             Func<DateTime>? now = null)
    {
        var config = new Config
        {
            SenderDiscordId = "123456789012345678",
            StableReads = stableReads,
            LabelThreshold = 0.80,
            DigitThreshold = 0.85,
        };
        var log = new List<string>();
        return (new Watcher(config, new ScreenReader(templates, config), null, log.Add, now), log);
    }

    private static List<string> Sent(List<string> log)
        => log.Where(l => l.Contains("score ") || l.Contains("code ")).ToList();

    [Fact]
    public async Task 同じ値が続くまで送らない()
    {
        var built = SyntheticScreen.Build(homeGoals: 4, awayGoals: 0);
        var (watcher, log) = Make(built.Templates, stableReads: 3);

        await watcher.StepAsync(built.Frame);
        Assert.Empty(Sent(log));
        await watcher.StepAsync(built.Frame);
        Assert.Empty(Sent(log));
        await watcher.StepAsync(built.Frame);
        Assert.Single(Sent(log));
    }

    [Fact]
    public async Task 同じ結果を二度送らない()
    {
        var built = SyntheticScreen.Build(homeGoals: 4, awayGoals: 0);
        var (watcher, log) = Make(built.Templates);
        for (int i = 0; i < 6; i++) await watcher.StepAsync(built.Frame);
        Assert.Single(Sent(log));
        Assert.Contains("score 123456789012345678 4 0 -", Sent(log)[0]);
    }

    [Fact]
    public async Task 側が読めないときは縁取りの画素数を手元に出す()
    {
        // ADR-0080 **合成画面には縁取りが無い**ので、側は `-` になる
        var built = SyntheticScreen.Build(homeGoals: 4, awayGoals: 0);
        var (watcher, log) = Make(built.Templates);
        for (int i = 0; i < 6; i++) await watcher.StepAsync(built.Frame);

        var notes = log.Where(l => l.Contains("縁取り")).ToList();
        Assert.Single(notes);                                           // 送った 1 回だけ
        Assert.Matches(@"ホーム側 \d+ 画素 / アウェイ側 \d+ 画素", notes[0]);
        Assert.DoesNotContain(Sent(log), l => l.Contains("画素"));      // 送る本文には入らない
    }

    [Fact]
    public async Task 同じルームコードを再送しない()
    {
        // ⚠ **Bot 側の `set_room_code` が毎回 `clear()` を呼ぶ。**
        // **再送のたびに両チームの準備完了が白紙に戻る**（CON-09 / UC-25 A5）
        var built = SyntheticScreen.Build(1, 0, gameCode: "330787");
        var (watcher, log) = Make(built.Templates);
        for (int i = 0; i < 8; i++) await watcher.StepAsync(built.Frame);
        Assert.Single(Sent(log));
        Assert.Contains("code 123456789012345678 330787", Sent(log)[0]);
    }

    [Fact]
    public async Task コードが変われば送る()
    {
        var first = SyntheticScreen.Build(1, 0, gameCode: "330787");
        var second = SyntheticScreen.Build(1, 0, gameCode: "142536");
        var (watcher, log) = Make(first.Templates);
        for (int i = 0; i < 3; i++) await watcher.StepAsync(first.Frame);
        for (int i = 0; i < 3; i++) await watcher.StepAsync(second.Frame);
        var sent = Sent(log);
        Assert.Equal(2, sent.Count);
        Assert.Contains("330787", sent[0]);
        Assert.Contains("142536", sent[1]);
    }

    [Fact]
    public async Task 読めない画面では何も送らない()
    {
        var built = SyntheticScreen.Build(homeGoals: 4, awayGoals: 0);
        var (watcher, log) = Make(built.Templates);
        var blank = new Frame(600, 400, 600 * 4, new byte[600 * 400 * 4]);
        for (int i = 0; i < 5; i++) await watcher.StepAsync(blank);
        Assert.Empty(Sent(log));
    }

    [Fact]
    public async Task 画面が変わっても_ちらつきでは二度送らない()
    {
        // 結果 → 一瞬読めない → また同じ結果、で 2 通目を出さない
        var built = SyntheticScreen.Build(homeGoals: 2, awayGoals: 1);
        var (watcher, log) = Make(built.Templates);
        var blank = new Frame(600, 400, 600 * 4, new byte[600 * 400 * 4]);
        for (int i = 0; i < 3; i++) await watcher.StepAsync(built.Frame);
        await watcher.StepAsync(blank);
        for (int i = 0; i < 3; i++) await watcher.StepAsync(built.Frame);
        Assert.Single(Sent(log));
    }

    [Fact]
    public async Task 結果の画面が1分消えていなければ_何周消えても次の試合とみなさない()
    {
        // ⚠ **周の数で数えない。** 1 周を 500 ms にすると、4 周は 2 秒しかない。
        // 画面の切り替えや別のウィンドウに出たくらいで、**同じ結果を二度送ってしまう**
        var clock = DateTime.UtcNow;
        var built = SyntheticScreen.Build(homeGoals: 2, awayGoals: 1);
        var (watcher, log) = Make(built.Templates, now: () => clock);
        var blank = new Frame(600, 400, 600 * 4, new byte[600 * 400 * 4]);

        for (int i = 0; i < 2; i++) await watcher.StepAsync(built.Frame);
        for (int i = 0; i < 10; i++) { clock += TimeSpan.FromSeconds(5); await watcher.StepAsync(blank); }   // 10 周・50 秒
        for (int i = 0; i < 2; i++) await watcher.StepAsync(built.Frame);
        Assert.Single(Sent(log));
    }

    [Fact]
    public async Task 結果の画面が1分以上消えていたら_同じ得点でも次の試合として送る()
    {
        // 前半と後半が同じ得点で終わることがある。**1 分空けば別の試合である**
        var clock = DateTime.UtcNow;
        var built = SyntheticScreen.Build(homeGoals: 2, awayGoals: 1);
        var (watcher, log) = Make(built.Templates, now: () => clock);
        var blank = new Frame(600, 400, 600 * 4, new byte[600 * 400 * 4]);

        for (int i = 0; i < 2; i++) await watcher.StepAsync(built.Frame);
        await watcher.StepAsync(blank);
        clock += TimeSpan.FromSeconds(61);
        for (int i = 0; i < 2; i++) await watcher.StepAsync(built.Frame);
        Assert.Equal(2, Sent(log).Count);
    }
}
