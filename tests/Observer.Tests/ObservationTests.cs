using RematchObserver;
using Xunit;

namespace RematchObserver.Tests;

/// <summary>
/// CON-09 の形。**契約なので、例をそのまま置いてある。**
///
/// ⚠ **CON-09 が正である。** ここが食い違ったら、直すのはこちらである
/// （ADR-0067 の見直し条件 6）。
/// </summary>
public class ObservationTests
{
    private const string Bot = "1539187275068473434";

    [Fact]
    public void 選手のコードは_メンションと種類と送り主とコード()
    {
        var line = Observation.Code(Bot, "123456789012345678", "330787", null);
        Assert.Equal($"<@{Bot}> code 123456789012345678 330787", line);
        Assert.Equal("code 123456789012345678 330787", Observation.Body(line));
    }

    [Fact]
    public void 運営のコードは_末尾に対戦idが付く()
    {
        var line = Observation.Code(Bot, "987654321098765432", "330787", 12);
        Assert.Equal("code 987654321098765432 330787 12", Observation.Body(line));
    }

    [Fact]
    public void 結果は_ホームとアウェイのゴール数を絶対値で送る()
    {
        var line = Observation.Score(Bot, "123456789012345678", 4, 0, Side.Home, null);
        Assert.Equal("score 123456789012345678 4 0 home", Observation.Body(line));
    }

    [Fact]
    public void 運営の結果は_側がハイフンで対戦idが付く()
    {
        // CON-09 の例そのまま。**運営の観戦では黄色の縁取りが出ないので側が分からない**
        var line = Observation.Score(Bot, "987654321098765432", 4, 0, Side.Unknown, 12);
        Assert.Equal("score 987654321098765432 4 0 - 12", Observation.Body(line));
    }

    [Fact]
    public void 側が分からなくても_項目は落とさない()
    {
        // **落とすと対戦 id の位置がずれる。** Bot は位置で読む
        var line = Observation.Score(Bot, "1", 2, 3, Side.Unknown, null);
        Assert.Equal("score 1 2 3 -", Observation.Body(line));
    }

    [Fact]
    public void 先頭のメンションが無ければ_送らない()
    {
        // ⚠ **飾りではない。** 無いと本文が Bot に届かない（特権インテントを使わない）
        var problems = Observation.Problems("score 123 4 0 home");
        Assert.Contains(problems, p => p.Contains("メンション"));
    }

    [Theory]
    [InlineData("<@1> code 123 33078")]      // 5 桁
    [InlineData("<@1> code 123 3307878")]    // 7 桁
    [InlineData("<@1> code 123 33078a")]     // 数字でない
    public void ゲームコードは_6桁の数字でなければ送らない(string line)
        => Assert.Contains(Observation.Problems(line), p => p.Contains("6 桁"));

    [Fact]
    public void 送り主が数字でなければ送らない()
    {
        // **Bot は id をサーバーの参加者として引き直す。** 数字でないと引けない
        Assert.Contains(Observation.Problems("<@1> score setopic 4 0 home"),
                        p => p.Contains("Discord の id"));
    }

    [Fact]
    public void ゴール数が負なら送らない()
        => Assert.Contains(Observation.Problems("<@1> score 123 -1 0 home"),
                           p => p.Contains("0 以上"));

    [Fact]
    public void 知らない種類は送らない()
        => Assert.Contains(Observation.Problems("<@1> goals 123 4 0"),
                           p => p.Contains("種類"));

    [Fact]
    public void 本文が長すぎれば送らない()
    {
        var line = "<@1> score 123 4 0 home " + new string('9', 300);
        Assert.Contains(Observation.Problems(line), p => p.Contains("長すぎ"));
    }

    [Fact]
    public void 正しい行には文句を付けない()
    {
        Assert.Empty(Observation.Problems(Observation.Code(Bot, "123456789012345678", "330787", null)));
        Assert.Empty(Observation.Problems(Observation.Score(Bot, "123456789012345678", 4, 0, Side.Away, 12)));
    }
}

public class ConfigTests
{
    [Fact]
    public void Webhookのurlは伏せて出す()
    {
        var shown = Config.Redacted("https://discord.com/api/webhooks/123456/abcdefSECRET");
        Assert.DoesNotContain("SECRET", shown);
        Assert.DoesNotContain("discord.com", shown);
    }

    [Fact]
    public void 送り主が空なら_足りないと言う()
    {
        var c = new Config { WebhookUrl = "https://example.invalid/x" };
        Assert.Contains(c.Problems(), p => p.Contains("senderDiscordId"));
    }

    [Fact]
    public void 既定のbotIdはCON09のもの()
        => Assert.Equal("1539187275068473434", new Config().BotUserId);

    [Fact]
    public void 書いて読み直しても同じ()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            new Config { SenderDiscordId = "42", MatchId = 12, PollIntervalMs = 900 }.Save(path);
            var back = Config.Load(path);
            Assert.Equal("42", back.SenderDiscordId);
            Assert.Equal(12, back.MatchId);
            Assert.Equal(900, back.PollIntervalMs);
        }
        finally { File.Delete(path); }
    }
}
