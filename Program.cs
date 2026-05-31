using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Discord;
using Discord.WebSocket;

class Program
{
    private DiscordSocketClient _client;
    private IMessageChannel _channel;

    private DateTime lastMorning = DateTime.MinValue;
    private DateTime lastEvening = DateTime.MinValue;

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        new Program().RunAsync().GetAwaiter().GetResult();
    }

    public async Task RunAsync()
    {
        _client = new DiscordSocketClient();

        string discordToken = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        string apiKey = Environment.GetEnvironmentVariable("FOOTBALL_API_KEY");

        ulong channelId = 1510639984636461147;

        if (string.IsNullOrWhiteSpace(discordToken) || string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("❌ Missing ENV variables");
            return;
        }

        await _client.LoginAsync(TokenType.Bot, discordToken);
        await _client.StartAsync();

        Console.WriteLine("🤖 Bot started (Cloud mode)");

        await Task.Delay(5000);

        _channel = _client.GetChannel(channelId) as IMessageChannel;

        if (_channel == null)
        {
            Console.WriteLine("❌ Channel not found or no permission");
            return;
        }

        await _channel.SendMessageAsync("🧪 Bot online (Cloud ready)");

        while (true)
        {
            try
            {
                var now = DateTime.Now;

                // ☀️ 08:00 - Fixtures
                if (now.Hour == 8 && now.Minute == 0)
                {
                    if (lastMorning.Date != now.Date)
                    {
                        await SendTodayFixtures(apiKey);
                        lastMorning = now;
                    }
                }

                // 🌙 23:59 - Results
                if (now.Hour == 23 && now.Minute == 59)
                {
                    if (lastEvening.Date != now.Date)
                    {
                        await SendDailySummary(apiKey);
                        lastEvening = now;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
            }

            await Task.Delay(30000);
        }
    }

    // =========================
    // ☀️ MORNING FIXTURES
    // =========================
    private async Task SendTodayFixtures(string apiKey)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("x-apisports-key", apiKey);

        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var res = await http.GetStringAsync(
            $"https://v3.football.api-sports.io/fixtures?date={today}"
        );

        var json = JObject.Parse(res);
        var matches = json["response"];

        var sb = new StringBuilder();

        sb.AppendLine("☀️ TODAY'S MATCHES");
        sb.AppendLine("━━━━━━━━━━━━━━━━━━━━\n");

        foreach (var m in matches)
        {
            string home = m["teams"]["home"]["name"]?.ToString();
            string away = m["teams"]["away"]["name"]?.ToString();

            string time = ToDiscordTime(m["fixture"]["date"]?.ToString());

            sb.AppendLine($"{GetFlag(m["teams"]["home"]["country"]?.ToString())} {home} 🆚 {away} {GetFlag(m["teams"]["away"]["country"]?.ToString())}");
            sb.AppendLine($"🕒 {time}");
            sb.AppendLine();
        }

        await SendLong(sb.ToString());
    }

    // =========================
    // 🌙 DAILY SUMMARY
    // =========================
    private async Task SendDailySummary(string apiKey)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("x-apisports-key", apiKey);

        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var res = await http.GetStringAsync(
            $"https://v3.football.api-sports.io/fixtures?date={today}"
        );

        var json = JObject.Parse(res);
        var matches = json["response"];

        var sb = new StringBuilder();

        sb.AppendLine("🌙 DAILY RESULTS");
        sb.AppendLine("━━━━━━━━━━━━━━━━━━━━\n");

        foreach (var m in matches)
        {
            string status = m["fixture"]["status"]["short"]?.ToString();

            if (status != "FT")
                continue;

            string home = m["teams"]["home"]["name"]?.ToString();
            string away = m["teams"]["away"]["name"]?.ToString();

            int hg = m["goals"]?["home"]?.Value<int?>() ?? 0;
            int ag = m["goals"]?["away"]?.Value<int?>() ?? 0;

            sb.AppendLine($"{GetFlag(m["teams"]["home"]["country"]?.ToString())} {home} {hg}-{ag} {away} {GetFlag(m["teams"]["away"]["country"]?.ToString())}");
            sb.AppendLine();
        }

        await SendLong(sb.ToString());
    }

    // =========================
    // ⏱ DISCORD TIME
    // =========================
    private string ToDiscordTime(string utcTime)
    {
        if (string.IsNullOrWhiteSpace(utcTime))
            return "?";

        DateTime utc = DateTime.Parse(utcTime).ToUniversalTime();
        long unix = ((DateTimeOffset)utc).ToUnixTimeSeconds();

        return $"<t:{unix}:R>";
    }

    // =========================
    // 🌍 FLAGS (GLOBAL SAFE)
    // =========================
    private string GetFlag(string country)
    {
        if (string.IsNullOrWhiteSpace(country))
            return "";

        country = country.Trim();

        if (country.Length == 2)
        {
            country = country.ToUpper();

            return char.ConvertFromUtf32(country[0] + 0x1F1A5) +
                   char.ConvertFromUtf32(country[1] + 0x1F1A5);
        }

        return country switch
        {
            "Argentina" => "🇦🇷",
            "Brazil" => "🇧🇷",
            "France" => "🇫🇷",
            "Germany" => "🇩🇪",
            "Spain" => "🇪🇸",
            "Italy" => "🇮🇹",
            "England" => "🇬🇧",

            "USA" or "United States" => "🇺🇸",
            "Mexico" => "🇲🇽",
            "Canada" => "🇨🇦",

            "Japan" => "🇯🇵",
            "South Korea" or "Korea Republic" => "🇰🇷",

            "Morocco" => "🇲🇦",
            "Senegal" => "🇸🇳",
            "Nigeria" => "🇳🇬",

            _ => ""
        };
    }

    // =========================
    // 📩 SAFE SENDER
    // =========================
    private async Task SendLong(string text)
    {
        const int max = 1900;

        for (int i = 0; i < text.Length; i += max)
        {
            string part = text.Substring(i, Math.Min(max, text.Length - i));
            await _channel.SendMessageAsync(part);
        }
    }
}