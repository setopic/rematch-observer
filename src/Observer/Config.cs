using System.Text.Json;
using System.Text.Json.Serialization;

namespace RematchObserver;

/// <summary>
/// 動かす人ごとの設定。
///
/// ⚠ **Webhook の URL は秘密である。** 画面にもログにも出さない
/// （<see cref="Redacted"/> を通す）。**漏れたら Discord 側で作り直す。**
/// </summary>
public sealed class Config
{
    /// <summary>Discord の Incoming Webhook。**ここだけが外に出る経路である。**</summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>
    /// Bot の Discord id。**本文の先頭に置くメンションに使う。**
    /// ⚠ 飾りではない。**これが無いと Bot に本文が届かない**（CON-09）。
    /// </summary>
    public string BotUserId { get; set; } = "1539187275068473434";

    /// <summary>
    /// 送り主の Discord id。**Bot がロールと所属を引き直す**（CON-09）。
    /// **ここで「自分は運営だ」と名乗ることはできない。**
    /// </summary>
    public string SenderDiscordId { get; set; } = "";

    /// <summary>
    /// 見ている対戦の id。**運営には要る**（所属を持たないので対戦を引けない）。
    /// **対戦表に `#12` の形で出ている**（CON-04）。選手は空でよい。
    /// </summary>
    public int? MatchId { get; set; }

    /// <summary>撮る相手。**プロセス名を先に見る**（タイトルより変わりにくい）。</summary>
    public string ProcessName { get; set; } = "Rematch";
    public string? WindowTitle { get; set; }

    public int PollIntervalMs { get; set; } = 1500;

    /// <summary>テンプレート画像の置き場所。exe からの相対でよい。</summary>
    public string TemplatesDir { get; set; } = "templates";

    /// <summary>ラベルの照合をどこで打ち切るか。**下げると静かに間違える。**</summary>
    public double LabelThreshold { get; set; } = 0.80;

    /// <summary>数字の照合の閾値。**ラベルより厳しくする**（1 桁の読み違いが致命的なので）。</summary>
    public double DigitThreshold { get; set; } = 0.85;

    /// <summary>
    /// 同じ値が何回続いたら送るか。**画面の切り替わりの途中を読まないための待ち。**
    /// ⚠ **誰も数字を見ないので**（ADR-0067）、ここが唯一の事前の防御である。
    /// </summary>
    public int StableReads { get; set; } = 3;

    /// <summary>
    /// 黄色の縁取りから自分の側を読むか。**既定は読まない。**
    /// ⚠ **側を間違えて送ると Bot が観測ごと捨てる**（UC-45 A3）ので、
    /// **校正できていないうちは `-`（分からない）で送るほうが良い。**
    /// </summary>
    public bool DetectSide { get; set; }

    /// <summary>
    /// `ゴール` 列のラベルの幅に対する、読み取り枠の幅の倍率。
    /// **ここと下の 2 つは、テンプレートを撮り直さずに枠を微調整するためにある。**
    /// </summary>
    public double CellWidthScale { get; set; } = 1.6;

    /// <summary>`合計マッチ数` のラベルの高さに対する、読み取り枠の高さの倍率。</summary>
    public double CellHeightScale { get; set; } = 1.6;

    /// <summary>`ゲームコード:` のラベルの高さに対する、数字を探す幅の倍率。</summary>
    public double CodeWidthScale { get; set; } = 8.0;

    // ---- 黄色の縁取り（detectSide が true のときだけ使う） ------------------
    // ⚠ **緑の背景は使わない。**あれはゲーム内で選択している選手に付くもので、
    // **運営の観戦では他人の行に付く**（CON-09）。

    public int OutlineMinRed { get; set; } = 190;
    public int OutlineMinGreen { get; set; } = 160;
    public int OutlineMaxBlue { get; set; } = 110;
    /// <summary>赤・緑が青からどれだけ離れていれば黄色とみなすか。</summary>
    public int OutlineMinSpread { get; set; } = 70;
    /// <summary>この画素数に届かなければ「縁取りは出ていない」とみなす。</summary>
    public int OutlineMinPixels { get; set; } = 400;

    /// <summary>送らずに画面に出すだけ。**最初の疎通の前に使う。**</summary>
    public bool DryRun { get; set; }

    // ---- 置き場所と読み書き ------------------------------------------------

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "rematch-observer", "config.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Config Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new Config();
        return JsonSerializer.Deserialize<Config>(File.ReadAllText(path), Json) ?? new Config();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>
    /// URL を人に見せる形。
    /// ⚠ **一部でも出さない。** Webhook の URL は**末尾がトークンそのもの**で、
    /// 見えた断片から総当たりの足がかりになる。**有無と長さだけを言う。**
    /// </summary>
    public static string Redacted(string url)
        => string.IsNullOrEmpty(url) ? "（未設定）" : $"（設定済み / {url.Length} 文字）";

    /// <summary>送る前に足りないものを並べる。**空なら送れる。**</summary>
    public List<string> Problems()
    {
        var bad = new List<string>();
        if (string.IsNullOrWhiteSpace(WebhookUrl)) bad.Add("webhookUrl が空です");
        else if (!WebhookUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            bad.Add("webhookUrl が https で始まっていません");
        if (!IsId(BotUserId)) bad.Add("botUserId が数字ではありません");
        if (!IsId(SenderDiscordId))
            bad.Add("senderDiscordId が数字ではありません（Bot が引けないと観測は捨てられます）");
        if (StableReads < 1) bad.Add("stableReads は 1 以上です");
        return bad;
    }

    private static bool IsId(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
}
