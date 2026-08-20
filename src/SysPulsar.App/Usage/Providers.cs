using System.IO;
using System.Net.Http;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SysPulsar.App.Usage;

public interface IUsageProvider
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>
    /// 使用量を取得する。認証ファイルは読み取り専用で開き、絶対に書き込まない
    /// （書き込むと本家 CLI のログインが壊れる）。
    /// HTTP 429 は RateLimitException を投げる。それ以外の失敗は例外を投げてよい
    /// （呼び出し側で直前値を保持する）。
    /// </summary>
    Task<UsageSnapshot> FetchAsync(HttpClient http, CancellationToken ct);
}

/// <summary>
/// Claude Code (Max/Pro) の使用量。
/// GET https://api.anthropic.com/api/oauth/usage
/// 認証: ~/.claude/.credentials.json → claudeAiOauth.accessToken（読むだけ）
/// 抽出: limits[] 配列のみ（トップレベルの重複キーは見ない）。
/// ※ 非公式API。スキーマ変更時は --debug の生JSONで確認して本クラスを直す。
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    public string Id => "claude";
    public string DisplayName => "Claude";

    static readonly string CredPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    public async Task<UsageSnapshot> FetchAsync(HttpClient http, CancellationToken ct)
    {
        var snap = new UsageSnapshot { ProviderId = Id, DisplayName = DisplayName };

        if (!File.Exists(CredPath))
        {
            snap.AuthMessage = "Claude Code にログインしてください";
            return snap;
        }

        string? token = null;
        try
        {
            // 本家が定期更新するため共有読み取りで開く。書き込みは絶対にしない。
            using var fs = new FileStream(CredPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var cred = await JsonDocument.ParseAsync(fs, cancellationToken: ct);
            if (cred.RootElement.TryGetProperty("claudeAiOauth", out var oa) &&
                oa.TryGetProperty("accessToken", out var at))
                token = at.GetString();
        }
        catch (Exception ex)
        {
            snap.Error = "認証ファイルの読み取りに失敗: " + ex.Message;
            return snap;
        }

        if (string.IsNullOrEmpty(token))
        {
            snap.AuthMessage = "Claude Code にログインしてください（accessToken なし）";
            return snap;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // anthropic-beta の値は変わり得る。401/400 が出たら --debug で確認して調整する。
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Headers.TryAddWithoutValidation("User-Agent", "usage-watcher/1.0");

        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            snap.AuthMessage = "トークン失効。Claude Code で再ログインしてください";
            return snap;
        }
        if (res.StatusCode == (HttpStatusCode)429)
            throw RateLimited(res);

        var body = await res.Content.ReadAsStringAsync(ct);
        Log.DumpDebugJson(Id, body);
        res.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(body);
        if (json.RootElement.TryGetProperty("limits", out var limits) &&
            limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var lim in limits.EnumerateArray())
            {
                var kind = lim.TryGetProperty("kind", out var k) ? k.GetString() : null;
                var pct = lim.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetDouble() : 0;
                DateTimeOffset? reset = null;
                if (lim.TryGetProperty("resets_at", out var r) &&
                    r.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var dto))
                    reset = dto;

                // 表示対象は Current session / All models の2本（週・モデル別は出さない）
                var label = kind switch
                {
                    "session" => "Current session",
                    "weekly_all" => "All models",
                    _ => null,
                };
                if (label == null) continue;

                snap.Gauges.Add(new GaugeInfo
                {
                    Label = label,
                    Percent = pct,
                    ResetAtUtc = reset,
                });
            }
        }
        return snap;
    }

    internal static RateLimitException RateLimited(HttpResponseMessage res)
    {
        var ra = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(300);
        if (ra < TimeSpan.FromSeconds(300)) ra = TimeSpan.FromSeconds(300);
        return new RateLimitException(ra);
    }
}

/// <summary>
/// Kimi Code (Kimi メンバーシップ) の使用量。
/// GET {base_url}/usages  （実測: https://api.kimi.com/coding/v1/usages → 200）
/// 認証(読むだけ原則)の優先順:
///   1. SysPulsar の config.json の kimiApiKey(Kimi Code Console で発行する API キー)
///   2. kimi-code config.toml の [providers.*] → api_key / base_url(Kimi Work 同梱ランタイム互換)
///   3. ~/.kimi-code/credentials/kimi-code.json の access_token
///      (新 Kimi Code CLI の OAuth 認証。15 分で切れるため CLI 使用直後しか有効でない。
///       あくまでフォールバック)
/// なお Total usage(totalQuota)は agent-gw 系の base_url でしか返らず、コンソールキー
/// (scope: FEATURE_CODING)では取れない。そのため 1 または 3 が主認証のときも、
/// config.toml に旧キーが残っていれば agent-gw を併せて叩き Total usage を補完する。
/// レスポンス: usage / limits[] / totalQuota { limit, used, remaining, resetTime(UTC) }
/// ※ 非公式API。CLI の /usage と同じ経路。スキーマ変更時は --debug で確認。
/// </summary>
public sealed class KimiProvider : IUsageProvider
{
    public string Id => "kimi";
    public string DisplayName => "Kimi";

    const string PublicBaseUrl = "https://api.kimi.com/coding/v1";

    readonly string? _configApiKey;

    /// <summary>configApiKey: SysPulsar の config.json の kimiApiKey(あれば最優先)。</summary>
    public KimiProvider(string? configApiKey = null)
    {
        _configApiKey = string.IsNullOrWhiteSpace(configApiKey) ? null : configApiKey.Trim();
    }

    static string[] CandidateConfigPaths() =>
    [
        // Kimi Code CLI ユーザ設定
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".kimi-code", "config.toml"),
        // Kimi Work (kimi-desktop) 同梱ランタイム
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "kimi-desktop", "daimon-share", "daimon", "runtime", "kimi-code", "config.toml"),
    ];

    public async Task<UsageSnapshot> FetchAsync(HttpClient http, CancellationToken ct)
    {
        var snap = new UsageSnapshot { ProviderId = Id, DisplayName = DisplayName };

        // 認証の解決(優先順はクラスコメント参照)
        string? token = null, baseUrl = null;
        var authSource = "";
        if (_configApiKey != null)
        {
            token = _configApiKey;
            baseUrl = PublicBaseUrl;
            authSource = "config";
        }
        else
        {
            if (TryReadTomlCredential(out var tomlKey, out var tomlUrl, out var tomlError))
            {
                token = tomlKey;
                baseUrl = tomlUrl;
                authSource = "toml";
            }
            else if (tomlError != null)
            {
                snap.Error = "設定ファイルの読み取りに失敗: " + tomlError;
                return snap;
            }
            else
            {
                token = ReadOAuthAccessToken();
                if (token != null)
                {
                    baseUrl = PublicBaseUrl;
                    authSource = "oauth";
                }
            }
        }

        if (token == null || baseUrl == null)
        {
            snap.AuthMessage = "Kimi の API キーが見つかりません"
                + "（config.json に kimiApiKey を設定するか、kimi で /login してください）";
            return snap;
        }

        // 取得先は2系統（実測済み）:
        //  - {設定の base_url}/usages      … Kimi Work 同梱の agent-gw は totalQuota（Total usage）だけ返す
        //  - https://api.kimi.com/coding/v1/usages … 公式ホスト。usage（7-day）と limits[300分窓]（5-hour）が返る
        // コンソールキー(scope: FEATURE_CODING)は agent-gw を叩けず(403 api_key_path_forbidden)、
        // 旧 toml キー(scope: FEATURE_WORK)は totalQuota だけ返す。両方あれば併用して全ゲージを埋める。
        var primaryUrl = baseUrl.TrimEnd('/') + "/usages";
        var publicUrl = PublicBaseUrl + "/usages";
        var endpoints = new List<(string Url, string Token)> { (primaryUrl, token) };
        if (!primaryUrl.Equals(publicUrl, StringComparison.OrdinalIgnoreCase))
            endpoints.Add((publicUrl, token));

        // Total usage 補完: 主認証が旧 toml キーでなくても、toml に有効なキーが
        // 残っていれば agent-gw も追加で叩く(失効していれば Total usage が出ないだけ)
        if (authSource != "toml" &&
            TryReadTomlCredential(out var supKey, out var supUrl, out _) &&
            supKey != token)
        {
            var agentUrl = supUrl!.TrimEnd('/') + "/usages";
            if (endpoints.All(e => !e.Url.Equals(agentUrl, StringComparison.OrdinalIgnoreCase)))
                endpoints.Add((agentUrl, supKey!));
        }

        var gotAny = false;
        Exception? primaryError = null;
        foreach (var (url, epToken) in endpoints)
        {
            var isPrimary = url == primaryUrl;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", epToken);
                req.Headers.TryAddWithoutValidation("User-Agent", "usage-watcher/1.0");
                using var res = await http.SendAsync(req, ct);

                if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    if (isPrimary)
                    {
                        snap.AuthMessage = authSource switch
                        {
                            "config" => "config.json の kimiApiKey が無効です"
                                + "（Kimi Code Console でキーを確認してください）",
                            "oauth" => "Kimi のアクセストークンが期限切れです"
                                + "（kimi CLI を一度使うと更新されます。恒久的には config.json に"
                                + " kimiApiKey を設定してください）",
                            _ => "Kimi の API キーが無効です（kimi で /login し直してください）",
                        };
                        return snap;
                    }
                    continue; // 補助側の失敗は無視（取れるものだけ表示）
                }
                if (res.StatusCode == (HttpStatusCode)429)
                {
                    if (isPrimary) throw ClaudeProvider.RateLimited(res);
                    continue;
                }
                if (!res.IsSuccessStatusCode)
                {
                    if (isPrimary) res.EnsureSuccessStatusCode();
                    continue;
                }

                var body = await res.Content.ReadAsStringAsync(ct);
                Log.DumpDebugJson(Id + (isPrimary ? "" : "-public"), body);
                using var json = JsonDocument.Parse(body);
                gotAny |= ParseUsages(json.RootElement, snap);
            }
            catch (RateLimitException) { throw; }
            catch (Exception ex) when (!isPrimary)
            {
                Log.Info("kimi: 補助エンドポイント失敗（続行）: " + ex.Message);
            }
            catch (Exception ex)
            {
                primaryError = ex;
            }
        }

        if (!gotAny && snap.Gauges.Count == 0)
        {
            if (primaryError != null) throw primaryError;
            snap.Error = "使用量データを取得できませんでした";
            return snap;
        }

        // 表示順: 5-hour usage → 7-day usage → Total usage
        snap.Gauges.Sort((a, b) => KimiGaugeOrder(a.Label).CompareTo(KimiGaugeOrder(b.Label)));
        return snap;
    }

    static int KimiGaugeOrder(string label) => label switch
    {
        "5-hour usage" => 0,
        "7-day usage" => 1,
        "Total usage" => 2,
        _ => 3,
    };

    /// <summary>/usages レスポンスから 5-hour / 7-day / Total の各枠を抽出して格上げする。</summary>
    static bool ParseUsages(JsonElement root, UsageSnapshot snap)
    {
        var found = false;

        // 7-day usage: usage { limit, used, resetTime }
        if (root.TryGetProperty("usage", out var usage) && GetNum(usage, "limit") > 0)
        {
            UpsertGauge(snap, "7-day usage", GetNum(usage, "used"), GetNum(usage, "limit"), GetReset(usage));
            found = true;
        }

        // 5-hour usage: limits[] のうち window = 300分（TIME_UNIT_MINUTE x 300）
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var lim in limits.EnumerateArray())
            {
                if (!lim.TryGetProperty("window", out var w)) continue;
                var unit = w.TryGetProperty("timeUnit", out var tu) ? tu.GetString() ?? "" : "";
                if (GetNum(w, "duration") != 300 ||
                    !unit.Contains("MINUTE", StringComparison.OrdinalIgnoreCase)) continue;
                if (!lim.TryGetProperty("detail", out var d) || GetNum(d, "limit") <= 0) continue;
                UpsertGauge(snap, "5-hour usage", GetNum(d, "used"), GetNum(d, "limit"), GetReset(d));
                found = true;
            }
        }

        // Total usage: totalQuota { limit, used, resetTime }
        if (root.TryGetProperty("totalQuota", out var q) && GetNum(q, "limit") > 0)
        {
            UpsertGauge(snap, "Total usage", GetNum(q, "used"), GetNum(q, "limit"), GetReset(q));
            found = true;
        }
        return found;
    }

    static void UpsertGauge(UsageSnapshot snap, string label, double used, double limit, DateTimeOffset? reset)
    {
        var pct = limit > 0 ? used / limit * 100.0 : 0;
        var g = snap.Gauges.FirstOrDefault(x => x.Label == label);
        if (g == null)
            snap.Gauges.Add(new GaugeInfo { Label = label, Percent = pct, ResetAtUtc = reset });
        else
        {
            g.Percent = pct;
            g.ResetAtUtc = reset;
        }
    }

    static DateTimeOffset? GetReset(JsonElement el)
    {
        if (el.TryGetProperty("resetTime", out var rt) &&
            rt.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(rt.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var dto))
            return dto;
        return null;
    }

    static double GetNum(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return 0;
    }

    /// <summary>kimi-code config.toml(Kimi Code CLI ユーザ設定 or Kimi Work 同梱)から
    /// api_key / base_url を読む。新 Kimi Code CLI は OAuth 方式で api_key が空のため、
    /// 値があるとき(Kimi Work 同梱ランタイム等)だけ true を返す。
    /// readError には読み取り例外のメッセージを返す(ファイル無し・キー空はエラー扱いしない)。</summary>
    static bool TryReadTomlCredential(out string? apiKey, out string? baseUrl, out string? readError)
    {
        apiKey = baseUrl = readError = null;
        // 候補を順に見て、api_key が実際に入っている最初の config.toml を採用する。
        // (先頭の ~/.kimi-code/config.toml は新 CLI の OAuth 方式で api_key が空。
        //  Kimi Work 同梱側に有効なキーが残っていることがある)
        foreach (var path in CandidateConfigPaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                var (k, u) = ParseKimiConfig(File.ReadAllLines(path));
                if (string.IsNullOrEmpty(k) || string.IsNullOrEmpty(u)) continue;
                apiKey = k;
                baseUrl = u;
                return true;
            }
            catch (Exception ex)
            {
                readError = ex.Message;
                return false;
            }
        }
        return false;
    }

    /// <summary>~/.kimi-code/credentials/kimi-code.json の access_token を読む(OAuth フォールバック)。
    /// 新 Kimi Code CLI の OAuth 認証ファイル。有効期限は 15 分で、kimi CLI が使われるたびに更新される。
    /// 読み取り専用。書き込みは本家 CLI のログインを壊す恐れがあるため絶対にしない。</summary>
    static string? ReadOAuthAccessToken()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".kimi-code", "credentials", "kimi-code.json");
            if (!File.Exists(path)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.TryGetProperty("access_token", out var t) &&
                t.ValueKind == JsonValueKind.String)
                return t.GetString();
        }
        catch
        {
            // フォールバックなので失敗しても黙って次へ
        }
        return null;
    }

    /// <summary>
    /// config.toml の [providers.*] ブロックから type="kimi" の api_key / base_url を拾う。
    /// 簡易パーサ（依存追加を避ける）。見つからなければ api_key / base_url を持つ最初のブロック。
    /// </summary>
    internal static (string? apiKey, string? baseUrl) ParseKimiConfig(string[] lines)
    {
        string? fallbackKey = null, fallbackUrl = null;
        string? curKey = null, curUrl = null, curType = null;
        var inProvider = false;

        (string?, string?) Flush()
        {
            if (inProvider && curKey != null && curUrl != null)
            {
                if (string.Equals(curType, "kimi", StringComparison.OrdinalIgnoreCase))
                    return (curKey, curUrl);
                fallbackKey ??= curKey;
                fallbackUrl ??= curUrl;
            }
            return (null, null);
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith('['))
            {
                var hit = Flush();
                if (hit.Item1 != null) return hit;
                inProvider = line.StartsWith("[providers.", StringComparison.Ordinal);
                curKey = curUrl = curType = null;
                continue;
            }
            if (!inProvider) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var name = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');
            switch (name)
            {
                case "type": curType = value; break;
                case "api_key": curKey = value; break;
                case "base_url": curUrl = value; break;
            }
        }
        var last = Flush();
        return last.Item1 != null ? last : (fallbackKey, fallbackUrl);
    }
}

/// <summary>
/// OpenCode Go のクォータ(残り利用枠)。
/// GET https://opencode.ai/zen/go/v1/usage
/// 認証: API キー(Bearer)。優先順:
///   1. 環境変数 OPENCODE_API_KEY
///   2. opencode.json / opencode.jsonc(グローバル ~/.config/opencode) の
///      provider.opencode.options.apiKey
///   3. auth.json(~/.local/share/opencode / %APPDATA%\opencode / %LOCALAPPDATA%\opencode) の
///      opencode-go または opencode エントリ { "type": "api", "key": "..." }
/// レスポンス: usage { rolling / weekly / monthly } の各 { status, percent(使用%), resetsAt(ISO+offset) }。
/// SysPulsar のゲージは「使用量%」表示のため、percent(使用%)をそのまま表示する。
/// ※ 非公式API。参考: https://github.com/slkiser/opencode-quota
/// </summary>
public sealed class OpenCodeGoProvider : IUsageProvider
{
    public string Id => "opencode-go";
    public string DisplayName => "OpenCode Go";

    const string UsageUrl = "https://opencode.ai/zen/go/v1/usage";

    readonly string? _configApiKey;

    /// <summary>configApiKey: SysPulsar の config.json の opencodeApiKey(あれば最優先)。</summary>
    public OpenCodeGoProvider(string? configApiKey = null)
    {
        _configApiKey = string.IsNullOrWhiteSpace(configApiKey) ? null : configApiKey.Trim();
    }

    static readonly string[] AuthJsonCandidates =
    [
        // xdg-basedir → ~/.local/share/opencode
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "opencode", "auth.json"),
        // Windows 互換のフォールバック
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "opencode", "auth.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "opencode", "auth.json"),
    ];

    static readonly string[] ConfigCandidates =
    [
        // グローバル設定(ローカル/カレントは信頼しない)。jsonc を優先
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "opencode", "opencode.jsonc"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "opencode", "opencode.json"),
    ];

    public async Task<UsageSnapshot> FetchAsync(HttpClient http, CancellationToken ct)
    {
        var snap = new UsageSnapshot { ProviderId = Id, DisplayName = DisplayName };

        // config.json の opencodeApiKey が最優先。なければ環境変数/opencode.json/auth.json を参照
        string? apiKey = _configApiKey;
        string source = "config.json";
        if (apiKey == null)
            apiKey = ResolveApiKey(out source);

        if (string.IsNullOrEmpty(apiKey))
        {
            snap.AuthMessage = "OpenCode Go の API キーが見つかりません"
                + "（config.json の opencodeApiKey、OPENCODE_API_KEY、opencode.json の"
                + " provider.opencode.options.apiKey、または auth.json を設定してください）";
            return snap;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("User-Agent", "usage-watcher/1.0");

        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            snap.AuthMessage = "OpenCode Go の API キーが無効です（" + source + "）";
            return snap;
        }
        if (res.StatusCode == (HttpStatusCode)429)
            throw ClaudeProvider.RateLimited(res);

        var body = await res.Content.ReadAsStringAsync(ct);
        Log.DumpDebugJson(Id, body);
        res.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            snap.Error = "OpenCode Go API レスポンスに usage がありません";
            return snap;
        }

        // rolling / weekly / monthly の順に表示
        var order = new[] { ("rolling", "5h"), ("weekly", "Weekly"), ("monthly", "Monthly") };
        foreach (var (key, label) in order)
        {
            if (!usage.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            if (!w.TryGetProperty("status", out var st) || st.GetString() != "ok") continue;
            var percent = w.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetDouble() : double.NaN;
            if (double.IsNaN(percent)) continue;

            DateTimeOffset? reset = null;
            if (w.TryGetProperty("resetsAt", out var r) &&
                r.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var dto))
                reset = dto;

            // SysPulsar のゲージは「使用量%」表示設計のため、percent をそのまま渡す
            // (残量の意味で使う UI ではない。0% 使用なら 0% 表示が正しい)
            snap.Gauges.Add(new GaugeInfo
            {
                Label = label,
                Percent = Math.Clamp(percent, 0, 100),
                ResetAtUtc = reset,
            });
        }

        if (snap.Gauges.Count == 0)
            snap.Error = "OpenCode Go の利用枠データを取得できませんでした";
        return snap;
    }

    /// <summary>API キーを解決する。見つからなければ null。source に由来を入れる。</summary>
    internal static string? ResolveApiKey(out string source)
    {
        // 1. 環境変数
        var env = Environment.GetEnvironmentVariable("OPENCODE_API_KEY")?.Trim();
        if (!string.IsNullOrEmpty(env))
        {
            source = "OPENCODE_API_KEY";
            return env;
        }

        // 2. opencode.json / opencode.jsonc(グローバル) の provider.opencode.options.apiKey
        foreach (var path in ConfigCandidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var config = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                var key = config?["provider"]?["opencode"]?["options"]?["apiKey"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    source = Path.GetFileName(path);
                    return key!.Trim();
                }
            }
            catch
            {
                // 壊れていても次へ
            }
        }

        // 3. auth.json(複数候補)の opencode-go / opencode エントリ
        foreach (var path in AuthJsonCandidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var auth = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                var entry = auth?["opencode-go"] ?? auth?["opencode"];
                var type = entry?["type"]?.GetValue<string>();
                var key = entry?["key"]?.GetValue<string>();
                if (type == "api" && !string.IsNullOrWhiteSpace(key))
                {
                    source = "auth.json(" + path + ")";
                    return key!.Trim();
                }
            }
            catch
            {
                // 次へ
            }
        }

        source = "";
        return null;
    }
}

/// <summary>
/// Cline (VS Code 拡張) の使用量(ローカル履歴の集計)。
/// Cline は「残量」を外部 API から取得せず、各タスクの
/// api_conversation_history.json にメッセージ毎のトークン数・推定コストを記録する。
/// ここでは ~/AppData/Roaming/Code/User/globalStorage/saoudrizwan.claude-dev/tasks/*/
/// 配下の履歴を読み、期間(今月/今週/合計)ごとのトークン使用量と推定コストを集計して表示する。
/// ※ API 呼び出しは不要。ローカルファイルの読み取りのみ(認証トークンは扱わない)。
/// </summary>
public sealed class ClineProvider : IUsageProvider
{
    public string Id => "cline";
    public string DisplayName => "Cline";

    static readonly string TasksDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "tasks");

    public async Task<UsageSnapshot> FetchAsync(HttpClient http, CancellationToken ct)
    {
        var snap = new UsageSnapshot { ProviderId = Id, DisplayName = DisplayName };

        if (!Directory.Exists(TasksDir))
        {
            snap.AuthMessage = "Cline (VS Code 拡張) の履歴が見つかりません";
            return snap;
        }

        // 全タスクの api_conversation_history.json を走査し、metrics.tokens / metrics.cost を集計
        long tokenPrompt = 0, tokenCompletion = 0, tokenCached = 0;
        double cost = 0;
        int fileCount = 0;
        var now = DateTimeOffset.Now;
        // 期間: 今月 / 今週 / 合計
        long monthPrompt = 0, monthCompletion = 0;
        double monthCost = 0;
        long weekPrompt = 0, weekCompletion = 0;
        double weekCost = 0;
        var weekStart = now.AddDays(-(int)now.DayOfWeek); // 週の開始(日曜)
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(TasksDir))
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(dir, "api_conversation_history.json");
                if (!File.Exists(path)) continue;
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                    foreach (var msg in doc.RootElement.EnumerateArray())
                    {
                        if (!msg.TryGetProperty("metrics", out var metrics) ||
                            metrics.ValueKind != JsonValueKind.Object) continue;
                        // メッセージのタイムスタンプ(期間判定用)
                        var ts = msg.TryGetProperty("ts", out var t) && t.ValueKind == JsonValueKind.Number
                            ? DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64()) : (DateTimeOffset?)null;

                        long prompt = 0, completion = 0, cached = 0;
                        if (metrics.TryGetProperty("tokens", out var tok) && tok.ValueKind == JsonValueKind.Object)
                        {
                            prompt = GetLong(tok, "prompt");
                            completion = GetLong(tok, "completion");
                            cached = GetLong(tok, "cached");
                        }
                        var msgCost = metrics.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number
                            ? c.GetDouble() : 0;

                        tokenPrompt += prompt;
                        tokenCompletion += completion;
                        tokenCached += cached;
                        cost += msgCost;
                        fileCount++;

                        if (ts != null)
                        {
                            if (ts >= monthStart) { monthPrompt += prompt; monthCompletion += completion; monthCost += msgCost; }
                            if (ts >= weekStart) { weekPrompt += prompt; weekCompletion += completion; weekCost += msgCost; }
                        }
                    }
                }
                catch
                {
                    // 1ファイルの失敗は無視して次へ
                }
            }
        }
        catch (OperationCanceledException) { throw; }

        if (fileCount == 0)
        {
            snap.Error = "Cline の使用量データがありません";
            return snap;
        }

        // ゲージ表示: トークン数と推定コストを「使用量」として数値表示(Percent は使用しない)
        long totalTokens = tokenPrompt + tokenCompletion;
        snap.Gauges.Add(new GaugeInfo
        {
            Label = "今月のトークン",
            Percent = 0,
            Text = FmtTokens(monthPrompt + monthCompletion),
        });
        snap.Gauges.Add(new GaugeInfo
        {
            Label = "今週のトークン",
            Percent = 0,
            Text = FmtTokens(weekPrompt + weekCompletion),
        });
        snap.Gauges.Add(new GaugeInfo
        {
            Label = "今月のコスト",
            Percent = 0,
            Text = $"${monthCost:F3}",
        });
        snap.Gauges.Add(new GaugeInfo
        {
            Label = "累計トークン",
            Percent = 0,
            Text = FmtTokens(totalTokens),
        });
        snap.Gauges.Add(new GaugeInfo
        {
            Label = "累計コスト",
            Percent = 0,
            Text = $"${cost:F3}",
        });
        return snap;
    }

    static long GetLong(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetInt64();
        return 0;
    }

    static string FmtTokens(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M" :
        n >= 1_000 ? $"{n / 1_000.0:F1}K" : $"{n}";
}
