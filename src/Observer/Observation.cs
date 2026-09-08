using System.Text.RegularExpressions;

namespace RematchObserver;

/// <summary>
/// CON-09 の 1 行を組み立てる。**これが契約である。**
///
/// <code>
/// &lt;@BOT&gt; code  &lt;送り主&gt; &lt;コード&gt; [&lt;対戦 id&gt;]
/// &lt;@BOT&gt; score &lt;送り主&gt; &lt;ホームのゴール&gt; &lt;アウェイのゴール&gt; &lt;側&gt; [&lt;対戦 id&gt;]
/// </code>
///
/// ⚠ **本文の先頭に Bot へのメンションを置く。飾りではない。**
/// これが無いと Bot に本文が届かない（特権インテントを使わないため）。
/// ⚠ **`allowed_mentions` で抑制してもいけない**（メンションとして数えられなくなる）。
///
/// ⚠ **送るのは数字だけである。** 画像も、選手名も、チーム名も、試合番号も送らない。
/// **これが個人情報の出ていく経路を塞ぐ唯一の手段である**（ADR-0067）。
///
/// ⚠ **CON-09 が正である**（写しは CON-09 と README の 2 か所まで）。
/// 形を変えるときは CON-09 →（実装と README）の順で直す。
/// </summary>
public static partial class Observation
{
    /// <summary>Bot 側の上限。**メンションを外した本文で数える**（Bot がそう数えている）。</summary>
    public const int MaxBody = 300;

    public static string Code(string botUserId, string senderId, string code, int? matchId)
        => Mention(botUserId) + Join("code", senderId, code, matchId?.ToString());

    public static string Score(string botUserId, string senderId, int home, int away,
                               Side side, int? matchId)
        // **側は常に付ける。** 位置で読まれるので、対戦 id を足すときに列がずれない
        => Mention(botUserId) + Join("score", senderId, $"{home} {away} {side.Wire()}", matchId?.ToString());

    private static string Mention(string botUserId) => $"<@{botUserId}> ";

    private static string Join(string kind, string sender, string middle, string? matchId)
        => matchId is null ? $"{kind} {sender} {middle}" : $"{kind} {sender} {middle} {matchId}";

    /// <summary>Bot が実際に読む部分。**メンションを外したもの。**</summary>
    public static string Body(string content) => MentionPattern().Replace(content, " ").Trim();

    [GeneratedRegex(@"<@!?\d+>")]
    private static partial Regex MentionPattern();

    /// <summary>
    /// 送る前の検査。**空なら送ってよい。**
    /// **通らないものを送らない**のは、Bot 側が黙って捨てるだけで理由が返らないためである。
    /// </summary>
    public static List<string> Problems(string content)
    {
        var bad = new List<string>();
        if (!content.StartsWith("<@", StringComparison.Ordinal))
            bad.Add("先頭が Bot へのメンションではありません（これが無いと本文が届きません）");
        var body = Body(content);
        if (body.Length == 0) bad.Add("本文が空です");
        if (body.Length > MaxBody) bad.Add($"本文が長すぎます（{body.Length} > {MaxBody}）");
        var parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) { bad.Add("項目が足りません"); return bad; }
        if (parts[0] is not ("code" or "score")) bad.Add($"種類が `code` でも `score` でもありません（{parts[0]}）");
        if (!parts[1].All(char.IsAsciiDigit))
            bad.Add("送り主が Discord の id ではありません（Bot がロールを引けません）");
        if (parts[0] == "code")
        {
            if (parts[2].Length != 6 || !parts[2].All(char.IsAsciiDigit))
                bad.Add($"ゲームコードが 6 桁の数字ではありません（{parts[2]}）");
        }
        else if (parts[0] == "score")
        {
            if (parts.Length < 4) { bad.Add("ゴール数が足りません"); return bad; }
            foreach (var g in new[] { parts[2], parts[3] })
                if (!int.TryParse(g, out int v) || v < 0) bad.Add($"ゴール数が 0 以上の整数ではありません（{g}）");
            if (parts.Length > 4 && parts[4] is not ("home" or "away" or "-"))
                bad.Add($"側が home / away / - のどれでもありません（{parts[4]}）");
        }
        return bad;
    }
}
