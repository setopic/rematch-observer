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

    /// <summary>
    /// 撮る相手。**プロセス名を先に見る**（タイトルより変わりにくい）。
    ///
    /// ⚠ **`Rematch` では見つからない。** Unreal のプロジェクト名が `Runtime` なので、
    /// **ウィンドウを持っているのは `RuntimeClient-Win64-Shipping`** である
    /// （Steam のフォルダ名だけが `Rematch`）。
    /// 部分一致で探すので `RuntimeClient` で足りる。
    /// **同じ罠がログの置き場所にもある**（`%LOCALAPPDATA%\Runtime\Saved\Logs`）。
    /// </summary>
    public string ProcessName { get; set; } = "RuntimeClient";
    public string? WindowTitle { get; set; }

    public int PollIntervalMs { get; set; } = 1500;

    /// <summary>テンプレート画像の置き場所。exe からの相対でよい。</summary>
    public string TemplatesDir { get; set; } = "templates";

    /// <summary>
    /// テンプレートの実際の場所。**相対パスは exe の隣として読む。**
    ///
    /// ⚠ **作業ディレクトリ基準にしない。** 常駐はショートカットやスタートアップから
    /// 起動されうるので、**どこから起動しても同じものを読ませる。**
    /// 作業ディレクトリ基準だと、**読めないのではなく違うものを読む**ことがある。
    /// </summary>
    public string ResolvedTemplatesDir => Path.IsPathRooted(TemplatesDir)
        ? TemplatesDir
        : Path.Combine(AppContext.BaseDirectory, TemplatesDir);

    /// <summary>ラベルの照合をどこで打ち切るか。**下げると静かに間違える。**</summary>
    public double LabelThreshold { get; set; } = 0.80;

    /// <summary>数字の照合の閾値。**ラベルより厳しくする**（1 桁の読み違いが致命的なので）。</summary>
    public double DigitThreshold { get; set; } = 0.85;

    /// <summary>
    /// 「字がある」とみなす明るさ。**読み落としの検出に使う。**
    /// 当たらなかった字が枠に残っていたら、その読みごと捨てる。
    /// </summary>
    public int InkThreshold { get; set; } = 150;

    /// <summary>
    /// 同じ値が何回続いたら送るか。**画面の切り替わりの途中を読まないための待ち。**
    /// ⚠ **誰も数字を見ないので**（ADR-0067）、ここが唯一の事前の防御である。
    /// </summary>
    public int StableReads { get; set; } = 3;

    /// <summary>
    /// 黄色の縁取りから自分の側を読むか。**既定で読む。**
    ///
    /// ⚠ **これは検算の材料ではない。得点の向きそのものである**（CON-09 / ADR-0071）。
    /// **ゲームの `ホーム` と、Bot が持つ対戦のホームは別物**で、
    /// **結びつける仕組みがどこにも無い**（ゲームの枠は選手が自分で選ぶ）。
    ///
    /// **送り主が画面のどちら側に居たか**だけが、両者を繋ぐ手がかりである。
    /// **送らないと、Bot は対戦のホームに重ねるしかない**（検証されていない仮定）。
    ///
    /// ⚠ **読めなければ `-` を送る。** 縁取りの判定は
    /// **反対側を大きく引き離していること**を求めるので、
    /// **間違った側を送るより、分からないと言うほうに倒れる。**
    /// </summary>
    public bool DetectSide { get; set; } = true;

    /// <summary>
    /// `ゴール` 列のラベルの幅に対する、読み取り枠の幅の倍率。
    /// **ここと下の 2 つは、テンプレートを撮り直さずに枠を微調整するためにある。**
    /// </summary>
    public double CellWidthScale { get; set; } = 1.6;

    /// <summary>`合計マッチ数` のラベルの高さに対する、読み取り枠の高さの倍率。</summary>
    public double CellHeightScale { get; set; } = 1.6;

    /// <summary>`ゲームコード:` のラベルの高さに対する、数字を探す幅の倍率。</summary>
    public double CodeWidthScale { get; set; } = 8.0;

    /// <summary>
    /// 同じく高さの倍率。⚠ **コードはラベルの「下の行」にある**（実画面で確認）。
    /// </summary>
    public double CodeHeightScale { get; set; } = 1.8;

    // ---- ヘッダ（試合の得点。CON-09） --------------------------------------
    // ⚠ **ヘッダには文字のラベルが無い。** ほかの読み場所と違い、位置に頼っている。
    // **画面の作りが変わったとき、ここが先に壊れる見込みである。**
    // 倍率はすべて `ホーム` ラベルの高さを 1 とした値。

    /// <summary>`ホーム` ラベルの何倍ぶん上から見るか。</summary>
    public double HeaderTopScale { get; set; } = 4.0;

    /// <summary>同じく、どこまでで打ち切るか。</summary>
    public double HeaderBottomScale { get; set; } = 2.0;

    /// <summary>時計の箱の左右、何倍ぶんを数字として見るか。**広げるとアイコンを巻き込む。**</summary>
    public double HeaderNumberScale { get; set; } = 2.0;

    /// <summary>列の何割が明るければ「時計の箱」とみなすか。</summary>
    public int TimerBrightPercent { get; set; } = 30;

    // ---- 黄色の縁取り（detectSide が true のときだけ使う） ------------------
    // ⚠ **緑の背景は使わない。**あれはゲーム内で選択している選手に付くもので、
    // **運営の観戦では他人の行に付く**（CON-09）。

    public int OutlineMinRed { get; set; } = 190;
    public int OutlineMinGreen { get; set; } = 160;
    public int OutlineMaxBlue { get; set; } = 110;
    /// <summary>赤・緑が青からどれだけ離れていれば黄色とみなすか。</summary>
    public int OutlineMinSpread { get; set; } = 70;
    /// <summary>この画素数に届かなければ「縁取りは出ていない」とみなす。</summary>
    public int OutlineMinPixels { get; set; } = 1500;

    /// <summary>
    /// 反対側の何倍あれば「縁取りが出ている側」とみなすか。
    /// ⚠ **星印と MVP の金色で、縁取りが無くても数百画素は出る**（実画面で確認）。
    /// **本物の縁取りは行を一周するので桁違いに多い。**
    /// </summary>
    public int OutlineDominance { get; set; } = 5;

    /// <summary>送らずに画面に出すだけ。**最初の疎通の前に使う。**</summary>
    public bool DryRun { get; set; }

    // ---- 置き場所と読み書き ------------------------------------------------

    /// <summary>exe の隣。**配布した 1 フォルダの中で完結する**ので、こちらを既定にする。</summary>
    public static string BesideExe => Path.Combine(AppContext.BaseDirectory, "config.json");

    /// <summary>以前の置き場所。**すでに書いた人の設定を捨てないために見る。**</summary>
    public static string InAppData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "rematch-observer", "config.json");

    /// <summary>
    /// 設定ファイルの場所。
    ///
    /// **exe の隣を先に見る。** 展開したフォルダごと渡せて、**消すのもフォルダごとで済む。**
    /// **`templates/` と同じ考え方である。**
    ///
    /// ⚠ **%APPDATA% にあるものも読む。** 先にそちらへ書いた人の設定を、
    /// **黙って無視して「未設定」に見せない。**
    /// ⚠ **exe を Program Files に置くと、隣に書けないことがある。**
    /// そのときは `--config` で場所を指すか、書ける所に置くこと。
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            if (File.Exists(BesideExe)) return BesideExe;
            return File.Exists(InAppData) ? InAppData : BesideExe;
        }
    }

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

/// <summary>人に見せる注意書き。**画面とコマンドで同じ文を使う。**</summary>
public static class Warnings
{
    /// <summary>
    /// ⚠ **既定を変えても、すでに書かれた設定ファイルは変わらない。**
    /// 古い版で作った人は `detectSide: false` のままなので、**気づけるように言う。**
    /// </summary>
    public const string SideOff =
        "⚠ detectSide が false です。得点の向きが Bot 側の推測になります"
        + "（config.json で true にしてください）";
}
