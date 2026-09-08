using System.Net;
using System.Text;
using System.Text.Json;

namespace RematchObserver;

/// <summary>
/// Discord の Incoming Webhook に 1 行を投げる。
///
/// **外向きに接続するだけである。** 受信するポートは開かない（ADR-0007 / ARCH-01）。
///
/// ⚠ **送るのは `content` だけ。** 画像も添付も送らない（ADR-0067）。
/// ⚠ **`allowed_mentions` を付けない。** 抑制するとメンションとして数えられず、
/// **本文が Bot に届かなくなる**（CON-09）。
/// ⚠ **URL をログに出さない。** 秘密である。
/// </summary>
public sealed class Webhook : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _url;

    public Webhook(string url, HttpClient? http = null)
    {
        _url = url;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("rematch-observer/1.0 (+https://github.com/setopic/rematch-observer)");
    }

    public sealed record Sent(bool Ok, HttpStatusCode Status, string? Reason);

    /// <summary>
    /// 1 通送る。**429 は Retry-After ぶんだけ待って 1 度だけやり直す。**
    /// **投げっぱなしにしない**のは、Bot が黙って捨てるので送信側の記録しか残らないためである。
    /// </summary>
    public async Task<Sent> SendAsync(string content, CancellationToken ct = default)
    {
        var problems = Observation.Problems(content);
        if (problems.Count > 0)
            return new Sent(false, 0, "送る前の検査で止めました: " + string.Join(" / ", problems));

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["content"] = content });
            using var body = new StringContent(json, Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try { response = await _http.PostAsync(_url, body, ct); }
            catch (HttpRequestException e) { return new Sent(false, 0, "送れません: " + e.Message); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            { return new Sent(false, 0, "送信が時間切れになりました"); }

            using (response)
            {
                if (response.IsSuccessStatusCode) return new Sent(true, response.StatusCode, null);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 1, 30)), ct);
                    continue;
                }
                // ⚠ 本体には URL が出ないが、念のため中身は載せない
                return new Sent(false, response.StatusCode, $"Discord が {(int)response.StatusCode} を返しました");
            }
        }
        return new Sent(false, HttpStatusCode.TooManyRequests, "流量の上限に当たり続けています");
    }

    public void Dispose() => _http.Dispose();
}
