using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Discord;
using Discord.WebSocket;

class Program
{
    private static readonly HttpClient Http = new HttpClient();

    private DiscordSocketClient _client;
    private IMessageChannel _channel;

    private DateTime _lastMorningSent = DateTime.MinValue;
    private DateTime _lastEveningSent = DateTime.MinValue;

    //WorldCup 2026 free API - no key required
    private const string WC_API_URL = "https://worldcup26.ir/get/games";

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        new Program().RunAsync().GetAwaiter().GetResult();
    }

    public async Task RunAsync()
    {
        string discordToken = Environment.GetEnvironmentVariable("DISCORD_TOKEN");

        ulong channelId = 1510639984636461147;

        if (string.IsNullOrWhiteSpace(discordToken))
        {
            Console.WriteLine("❌ Missing DISCORD_TOKEN env variable");
            return;
        }

        _client = new DiscordSocketClient();

        await _client.LoginAsync(TokenType.Bot, discordToken);
        await _client.StartAsync();

        Console.WriteLine("🤖 Bot started");

        await Task.Delay(5000);

        _channel = _client.GetChannel(channelId) as IMessageChannel;
        if (_channel == null)
        {
            Console.WriteLine("❌ Channel not found or no permission");
            return;
        }

        await _channel.SendMessageAsync("🧪 Bot online");

        while (true)
        {
            try
            {
                var now = DateTime.UtcNow;

                // Morning: send today's fixtures once per UTC day at 09:00 UTC
                var morningRunTime = now.Date.AddHours(9);
                if (now >= morningRunTime && now < morningRunTime.AddMinutes(1))
                {
                    if (_lastMorningSent.Date != now.Date)
                    {
                        await SendTodayFixtures(now.Date);
                        _lastMorningSent = now;
                    }
                }

                // Evening: send finished match results at 23:59 UTC
                var eveningRunTime = now.Date.AddHours(23).AddMinutes(59);
                if (now >= eveningRunTime && now < eveningRunTime.AddMinutes(1))
                {
                    if (_lastEveningSent.Date != now.Date)
                    {
                        await SendDailySummary(now.Date);
                        _lastEveningSent = now;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Loop error: " + ex.Message);
            }

            await Task.Delay(15000);
        }
    }

    // ── Fetch all WC matches for a given UTC date ──────────────────────────
    private async Task<JArray> GetWCMatchesForDateAsync(DateTime utcDate)
    {
        string responseText = await Http.GetStringAsync(WC_API_URL);
        var allMatches = JArray.Parse(responseText);

        // local_date format from the API: "MM/dd/yyyy HH:mm"
        string datePrefix = utcDate.ToString("MM/dd/yyyy");

        var todayMatches = new JArray();
        foreach (var m in allMatches)
        {
            string localDate = m["local_date"]?.ToString() ?? "";
            if (localDate.StartsWith(datePrefix))
                todayMatches.Add(m);
        }

        Console.WriteLine($"📅 {datePrefix} → {todayMatches.Count} matches found");
        return todayMatches;
    }

    // ── Morning: post today's upcoming fixtures ────────────────────────────
    private async Task SendTodayFixtures(DateTime utcDate)
    {
        var matches = await GetWCMatchesForDateAsync(utcDate);

        if (matches.Count == 0)
        {
            await _channel.SendMessageAsync(
                "☀️ TODAY'S MATCHES\n━━━━━━━━━━━━━━━━━━━━\n\n😴 No World Cup matches today.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("☀️ TODAY'S MATCHES");
        sb.AppendLine("━━━━━━━━━━━━━━━━━━━━\n");

        foreach (var m in matches)
        {
            string home = m["home_team_name_en"]?.ToString() ?? "Unknown";
            string away = m["away_team_name_en"]?.ToString() ?? "Unknown";
            string group = m["group"]?.ToString() ?? "?";
            string localDate = m["local_date"]?.ToString() ?? "";
            string matchday = m["matchday"]?.ToString() ?? "?";

            string homeFlag = GetFlag(home);
            string awayFlag = GetFlag(away);
            string discordTime = LocalDateToDiscordTime(localDate);

            sb.AppendLine($"🏆 Group {group} — Matchday {matchday}");
            sb.AppendLine($"{homeFlag} {home} 🆚 {away} {awayFlag}");
            sb.AppendLine($"🕒 {discordTime}");
            sb.AppendLine();
        }

        await SendLong(sb.ToString());
    }

    // ── Evening: post finished match results ──────────────────────────────
    private async Task SendDailySummary(DateTime utcDate)
    {
        var matches = await GetWCMatchesForDateAsync(utcDate);

        if (matches.Count == 0)
        {
            await _channel.SendMessageAsync(
                "🌙 DAILY RESULTS\n━━━━━━━━━━━━━━━━━━━━\n\n😴 No World Cup matches today.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("🌙 DAILY RESULTS");
        sb.AppendLine("━━━━━━━━━━━━━━━━━━━━\n");

        bool anyFinished = false;

        foreach (var m in matches)
        {
            string finished = m["finished"]?.ToString() ?? "FALSE";
            if (!string.Equals(finished, "TRUE", StringComparison.OrdinalIgnoreCase))
                continue;

            anyFinished = true;

            string home = m["home_team_name_en"]?.ToString() ?? "Unknown";
            string away = m["away_team_name_en"]?.ToString() ?? "Unknown";
            string homeScore = m["home_score"]?.ToString() ?? "0";
            string awayScore = m["away_score"]?.ToString() ?? "0";
            string group = m["group"]?.ToString() ?? "?";
            string matchday = m["matchday"]?.ToString() ?? "?";

            string homeFlag = GetFlag(home);
            string awayFlag = GetFlag(away);

            sb.AppendLine($"🏆 Group {group} — Matchday {matchday}");
            sb.AppendLine($"{homeFlag} {home} {homeScore}-{awayScore} {away} {awayFlag}");
            sb.AppendLine();

            // Scorers - format unknown until we see a real finished match.
            // Will be updated once we know what the API returns.
            AppendScorers(sb, m["home_scorers"]?.ToString(), homeFlag);
            AppendScorers(sb, m["away_scorers"]?.ToString(), awayFlag);

            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine();
        }

        if (!anyFinished)
            sb.AppendLine("⏳ No finished matches yet.");

        await SendLong(sb.ToString());
    }

    // ── Scorers helper ────────────────────────────────────────────────────
    // NOTE: scorer format is unknown until we see a real finished match.
    // For now just prints the raw string. Update this once we see real data.
    private void AppendScorers(StringBuilder sb, string scorers, string flag)
    {
        if (string.IsNullOrWhiteSpace(scorers) || scorers == "null")
            return;

        sb.AppendLine($"⚽ {flag} {scorers}");
        sb.AppendLine();
    }

    // ── Convert local_date string → Discord relative timestamp ────────────
    // local_date = "MM/dd/yyyy HH:mm" in stadium local time.
    // WARNING: these times are NOT UTC — they vary by stadium (-4 to -7).
    // Treated as UTC for now; can add a stadium offset lookup later.
    private string LocalDateToDiscordTime(string localDate)
    {
        if (string.IsNullOrWhiteSpace(localDate))
            return "?";

        if (!DateTime.TryParseExact(
                localDate,
                "MM/dd/yyyy HH:mm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTime dt))
        {
            return localDate; // fallback: show raw string
        }

        long unix = ((DateTimeOffset)dt).ToUnixTimeSeconds();
        return $"<t:{unix}:R>";
    }

    // ── Map team name → flag emoji ────────────────────────────────────────
    private string GetFlag(string teamName)
    {
        if (string.IsNullOrWhiteSpace(teamName))
            return "";

        return TeamNameToIso2.TryGetValue(teamName.Trim(), out string iso2)
            ? Iso2ToFlag(iso2)
            : "";
    }

    private string Iso2ToFlag(string iso2)
    {
        if (string.IsNullOrWhiteSpace(iso2) || iso2.Length != 2)
            return "";

        char a = char.ToUpperInvariant(iso2[0]);
        char b = char.ToUpperInvariant(iso2[1]);

        if (a < 'A' || a > 'Z' || b < 'A' || b > 'Z')
            return "";

        return char.ConvertFromUtf32(0x1F1E6 + (a - 'A')) +
               char.ConvertFromUtf32(0x1F1E6 + (b - 'A'));
    }

    // Maps the English team names as they appear in the WC API
    private static readonly Dictionary<string, string> TeamNameToIso2 = new(StringComparer.OrdinalIgnoreCase)
    {
        // Group A
        ["Mexico"] = "MX",
        ["South Africa"] = "ZA",
        ["South Korea"] = "KR",
        ["Czech Republic"] = "CZ",
        // Group B
        ["Canada"] = "CA",
        ["Bosnia & Herzegovina"] = "BA",
        ["Bosnia and Herzegovina"] = "BA",
        ["Qatar"] = "QA",
        ["Switzerland"] = "CH",
        // Group C
        ["Brazil"] = "BR",
        ["Morocco"] = "MA",
        ["Haiti"] = "HT",
        ["Scotland"] = "GB",
        // Group D
        ["USA"] = "US",
        ["United States"] = "US",
        ["Paraguay"] = "PY",
        ["Australia"] = "AU",
        ["Turkey"] = "TR",
        // Group E
        ["Germany"] = "DE",
        ["Curaçao"] = "CW",
        ["Curacao"] = "CW",
        ["Ivory Coast"] = "CI",
        ["Ecuador"] = "EC",
        // Group F
        ["Netherlands"] = "NL",
        ["Japan"] = "JP",
        ["Sweden"] = "SE",
        ["Tunisia"] = "TN",
        // Group G
        ["Belgium"] = "BE",
        ["Egypt"] = "EG",
        ["Iran"] = "IR",
        ["New Zealand"] = "NZ",
        // Group H
        ["Spain"] = "ES",
        ["Cape Verde"] = "CV",
        ["Saudi Arabia"] = "SA",
        ["Uruguay"] = "UY",
        // Group I
        ["France"] = "FR",
        ["Senegal"] = "SN",
        ["Iraq"] = "IQ",
        ["Norway"] = "NO",
        // Group J
        ["Argentina"] = "AR",
        ["Algeria"] = "DZ",
        ["Austria"] = "AT",
        ["Jordan"] = "JO",
        // Group K
        ["Portugal"] = "PT",
        ["DR Congo"] = "CD",
        ["Democratic Republic of the Congo"] = "CD",
        ["Uzbekistan"] = "UZ",
        ["Colombia"] = "CO",
        // Group L
        ["England"] = "GB",
        ["Croatia"] = "HR",
        ["Ghana"] = "GH",
        ["Panama"] = "PA",
    };

    // ── Split and send long messages (Discord 2000 char limit) ────────────
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