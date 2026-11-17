using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SysPulsar.App;

/// <summary>
/// config.json(実行ファイルと同じフォルダ)。PC 固有の設定はここに出す
/// (Python 版の DISK_ORDER / DISK_LABELS のハードコード解消)。
/// disks が空なら Online ディスクを番号順に最大 8 台自動検出。
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("intervalMs")] public int IntervalMs { get; set; } = 1000;
    [JsonPropertyName("namesRefreshSec")] public int NamesRefreshSec { get; set; } = 30;
    [JsonPropertyName("disks")] public List<DiskEntry> Disks { get; set; } = new();
    /// <summary>Kimi Code Console で発行する API キー(sk-...)。AI Usage の Kimi 側認証で最優先。</summary>
    [JsonPropertyName("kimiApiKey")] public string KimiApiKey { get; set; } = "";
    /// <summary>OpenCode Go の API キー。AI Usage の OpenCode Go 認証で最優先。
    /// 未設定なら OPENCODE_API_KEY 環境変数 / opencode.json / auth.json を参照する。</summary>
    [JsonPropertyName("opencodeApiKey")] public string OpenCodeApiKey { get; set; } = "";
    /// <summary>OpenAI 組織の Admin API キー(sk-admin-...)。AI Usage の OpenAI 残クレジットで使う
    /// (Costs API はこのキー種別でしか叩けない)。未設定なら OPENAI_ADMIN_KEY 環境変数を参照する。</summary>
    [JsonPropertyName("openaiAdminKey")] public string OpenAIAdminKey { get; set; } = "";
    /// <summary>OpenAI の基準時点の残高(USD。Billing ページの値)。これから openaiCreditAsOf 以降の
    /// 利用額を引いて残クレジットを出す。文字列 "12.5" でも読めるようにする(型違いで
    /// config.json 全体が既定値に戻るのを防ぐ)。</summary>
    [JsonPropertyName("openaiCreditBalance")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? OpenAICreditBalance { get; set; }
    /// <summary>openaiCreditBalance を見た日時(例: "2026-11-17 10:30"。オフセット無しはローカル時刻)。</summary>
    [JsonPropertyName("openaiCreditAsOf")] public string OpenAICreditAsOf { get; set; } = "";

    public sealed class DiskEntry
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("label")] public string Label { get; set; } = "";
    }

    public static AppConfig Load()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (config != null)
                    return config;
            }
        }
        catch
        {
            // 設定が壊れていても既定値で起動する
        }
        return new AppConfig();
    }
}
