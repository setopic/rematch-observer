using System.Runtime.Versioning;

namespace RematchObserver;

/// <summary>
/// 常駐して画面を見続ける。
///
/// **変化が無ければ送らない。** ⚠ **同じルームコードを再送してはいけない**（CON-09）。
/// Bot 側の `set_room_code` が毎回 `clear()` を呼ぶので、
/// **再送のたびに両チームの準備完了が白紙に戻る。**
///
/// **同じ値が続けて読めるまで送らない**（config の stableReads）。
/// ⚠ **完全自動なので誰も数字を見ない**（ADR-0067）。
/// **入る前に止められる手段はここしか無い。**
///
/// ⚠ **画像を保存しない。** 校正のときだけ、明示的に指示された場合に限る。
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class Watcher
{
    private readonly Config _config;
    private readonly ScreenReader _reader;
    private readonly Webhook? _webhook;
    private readonly Action<string> _log;

    private string? _candidate;
    private int _candidateCount;

    private string? _sentCode;
    private string? _sentResult;
    private DateTime _sentResultAt = DateTime.MinValue;
    private int _framesWithoutResult;

    /// <summary>同じ結果をもう一度送れるようになるまでの間。**画面のちらつきで二重に送らない。**</summary>
    private static readonly TimeSpan ResultCooldown = TimeSpan.FromMinutes(3);

    /// <summary>結果の画面が何周期消えたら「次の試合」とみなすか。</summary>
    private const int ScreenGoneFrames = 4;

    public Watcher(Config config, ScreenReader reader, Webhook? webhook, Action<string> log)
    {
        _config = config; _reader = reader; _webhook = webhook; _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Capture? capture = null;
        nint captured = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var window = WindowFinder.Find(_config.ProcessName, _config.WindowTitle);
                if (window is null)
                {
                    if (capture is not null) { _log("ウィンドウが消えました。待ちます"); capture.Dispose(); capture = null; captured = 0; }
                    await Delay(ct);
                    continue;
                }
                if (capture is null || captured != window.Value.Handle)
                {
                    capture?.Dispose();
                    capture = new Capture(window.Value.Handle);
                    captured = window.Value.Handle;
                    _log($"見ています: {window.Value}");
                }

                Frame? frame = null;
                try { frame = await capture.GrabAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception e)
                {
                    // **落ちない。** 画面の切り替えやフルスクリーンの往復でよく失敗する
                    _log("キャプチャに失敗しました: " + e.Message);
                    capture.Dispose(); capture = null; captured = 0;
                }
                if (frame is not null) await StepAsync(frame, ct);
                await Delay(ct);
            }
        }
        finally { capture?.Dispose(); }
    }

    private Task Delay(CancellationToken ct)
        => Task.Delay(Math.Max(200, _config.PollIntervalMs), ct);

    /// <summary>1 枚ぶんの判断。**テストから直に呼べるようにしてある。**</summary>
    public async Task StepAsync(Frame frame, CancellationToken ct = default)
    {
        var code = _reader.ReadGameCode(frame);
        if (code is not null)
        {
            if (Stable("code:" + code.Code)) await SendCodeAsync(code.Code, ct);
            return;
        }

        var result = _reader.ReadResult(frame);
        if (result is null)
        {
            if (++_framesWithoutResult >= ScreenGoneFrames) _sentResult = null;   // 次の試合を塞がない
            _candidate = null; _candidateCount = 0;
            return;
        }
        _framesWithoutResult = 0;
        var key = $"score:{result.HomeGoals}:{result.AwayGoals}:{result.Side.Wire()}";
        if (Stable(key)) await SendResultAsync(result, key, ct);
    }

    /// <summary>同じ読みが続いたか。**続いた回数が足りたときだけ true を 1 度返す。**</summary>
    private bool Stable(string key)
    {
        if (_candidate == key) _candidateCount++;
        else { _candidate = key; _candidateCount = 1; }
        if (_candidateCount != Math.Max(1, _config.StableReads)) return false;
        _candidateCount++;                       // 同じ読みが続いても 2 度は返さない
        return true;
    }

    private async Task SendCodeAsync(string code, CancellationToken ct)
    {
        // ⚠ **同じコードを再送しない。** 準備完了が白紙に戻る（CON-09 / UC-25 A5）
        if (_sentCode == code) return;
        var line = Observation.Code(_config.BotUserId, _config.SenderDiscordId, code, _config.MatchId);
        if (await SendAsync(line, ct)) _sentCode = code;
    }

    private async Task SendResultAsync(ResultReading result, string key, CancellationToken ct)
    {
        if (_sentResult == key && DateTime.UtcNow - _sentResultAt < ResultCooldown) return;
        var line = Observation.Score(_config.BotUserId, _config.SenderDiscordId,
                                     result.HomeGoals, result.AwayGoals, result.Side, _config.MatchId);
        if (await SendAsync(line, ct)) { _sentResult = key; _sentResultAt = DateTime.UtcNow; }
    }

    private async Task<bool> SendAsync(string line, CancellationToken ct)
    {
        if (_config.DryRun || _webhook is null)
        {
            _log($"（送っていません）{line}");
            return true;
        }
        var sent = await _webhook.SendAsync(line, ct);
        _log(sent.Ok ? $"送りました: {line}" : $"送れませんでした（{sent.Reason}）: {line}");
        return sent.Ok;
    }
}
