using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
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

    private const int FriendliesLeagueId = 10;

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        new Program().RunAsync().GetAwaiter().GetResult();
    }

    public async Task RunAsync()
    {
        string discordToken = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        string apiKey = Environment.GetEnvironmentVariable("FOOTBALL_API_KEY");

        ulong channelId = 1510639984636461147;

        if (string.IsNullOrWhiteSpace(discordToken) || string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("❌ Missing ENV variables");
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

                // Morning: send today's fixtures once per UTC day
                var morningRunTime = now.Date.AddHours(9); // 09:00 UTC
                if (now >= morningRunTime && now < morningRunTime.AddMinutes(1))
                {
                    if (_lastMorningSent.Date != now.Date)
                    {
                        await SendTodayFixtures(apiKey, now.Date);
                        _lastMorningSent = now;
                    }
                }

                // Evening: send finished matches once per UTC day
                var eveningRunTime = now.Date.AddHours(23).AddMinutes(59); // 23:59 UTC
                if (now >= eveningRunTime && now < eveningRunTime.AddMinutes(1))
                {
                    if (_lastEveningSent.Date != now.Date)
                    {
                        await SendDailySummary(apiKey, now.Date);
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

    private async Task SendTodayFixtures(string apiKey, DateTime utcDate)
    {
        var fixtures = await GetFriendlyFixturesForDateAsync(apiKey, utcDate);

        if (fixtures.Count == 0)
        {
            await _channel.SendMessageAsync(
                "☀️ TODAY'S MATCHES\n━━━━━━━━━━━━━━━━━━━━\n\n😴 No friendly matches found for today (UTC).");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("☀️ TODAY'S MATCHES");
        sb.AppendLine("━━━━━━━━━━━━━━━━━━━━\n");

        foreach (var m in fixtures)
        {
            string home = m["teams"]?["home"]?["name"]?.ToString() ?? "Unknown";
            string away = m["teams"]?["away"]?["name"]?.ToString() ?? "Unknown";

            string homeCountry = m["teams"]?["home"]?["country"]?.ToString();
            string awayCountry = m["teams"]?["away"]?["country"]?.ToString();

            string matchUtc = m["fixture"]?["date"]?.ToString();
            string discordTime = ToDiscordRelativeTime(matchUtc);

            sb.AppendLine($"{GetFlag(homeCountry)} {home} 🆚 {away} {GetFlag(awayCountry)}");
            sb.AppendLine($"🕒 {discordTime}");
            sb.AppendLine();
        }

        await SendLong(sb.ToString());
    }

    private async Task SendDailySummary(string apiKey, DateTime utcDate)
    {
        var fixtures = await GetFriendlyFixturesForDateAsync(apiKey, utcDate);

        if (fixtures.Count == 0)
        {
            await _channel.SendMessageAsync(
                "🌙 DAILY RESULTS\n━━━━━━━━━━━━━━━━━━━━\n\n😴 No friendly matches found for today (UTC).");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("🌙 DAILY RESULTS");
        sb.AppendLine("━━━━━━━━━━━━━━━━━━━━\n");

        bool foundAnyFinished = false;

        foreach (var m in fixtures)
        {
            string status = m["fixture"]?["status"]?["short"]?.ToString();

            // FT = finished. If you also want AET / PEN, add them here.
            if (status != "FT")
                continue;

            foundAnyFinished = true;

            string home = m["teams"]?["home"]?["name"]?.ToString() ?? "Unknown";
            string away = m["teams"]?["away"]?["name"]?.ToString() ?? "Unknown";

            int homeGoals = m["goals"]?["home"]?.Value<int?>() ?? 0;
            int awayGoals = m["goals"]?["away"]?.Value<int?>() ?? 0;

            int fixtureId = m["fixture"]?["id"]?.Value<int>() ?? 0;

            string homeCountry = m["teams"]?["home"]?["country"]?.ToString();
            string awayCountry = m["teams"]?["away"]?["country"]?.ToString();

            string homeFlag = GetFlag(homeCountry);
            string awayFlag = GetFlag(awayCountry);

            sb.AppendLine($"{homeFlag} {home} {homeGoals}-{awayGoals} {away} {awayFlag}");
            sb.AppendLine();

            await AppendGoals(sb, apiKey, fixtureId, home, away, homeFlag, awayFlag);

            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine();
        }

        if (!foundAnyFinished)
        {
            sb.AppendLine("😴 No finished matches yet.");
        }

        await SendLong(sb.ToString());
    }

    private async Task AppendGoals(
        StringBuilder sb,
        string apiKey,
        int fixtureId,
        string homeTeam,
        string awayTeam,
        string homeFlag,
        string awayFlag)
    {
        try
        {
            string url = $"https://v3.football.api-sports.io/fixtures/events?fixture={fixtureId}";
            string responseText = await SendApiRequestAsync(apiKey, url);

            var json = JObject.Parse(responseText);
            var events = json["response"] as JArray ?? new JArray();

            var goalEvents = new List<JObject>();

            foreach (var ev in events)
            {
                string type = ev["type"]?.ToString();
                if (type == "Goal")
                    goalEvents.Add((JObject)ev);
            }

            if (goalEvents.Count == 0)
            {
                sb.AppendLine("😴 No goals scored");
                sb.AppendLine();
                return;
            }

            foreach (var ev in goalEvents.OrderBy(e => e["time"]?["elapsed"]?.Value<int>() ?? 0)
                                          .ThenBy(e => e["time"]?["extra"]?.Value<int?>() ?? 0))
            {
                int elapsed = ev["time"]?["elapsed"]?.Value<int>() ?? 0;
                int extra = ev["time"]?["extra"]?.Value<int>() ?? 0;

                string minute = extra > 0 ? $"{elapsed}+{extra}'" : $"{elapsed}'";

                string scorer = ev["player"]?["name"]?.ToString() ?? "Unknown";
                string assist = ev["assist"]?["name"]?.ToString();

                string team = ev["team"]?["name"]?.ToString() ?? "";

                string flag =
                    string.Equals(team, homeTeam, StringComparison.OrdinalIgnoreCase)
                        ? homeFlag
                        : string.Equals(team, awayTeam, StringComparison.OrdinalIgnoreCase)
                            ? awayFlag
                            : "";

                sb.AppendLine($"🕒 {minute}");
                sb.AppendLine($"⚽ {flag} {scorer}");

                if (!string.IsNullOrWhiteSpace(assist))
                    sb.AppendLine($"🎯 {assist}");

                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Events error for fixture {fixtureId}: {ex.Message}");
            sb.AppendLine("⚠️ Could not load goals for this match");
            sb.AppendLine();
        }
    }

    private async Task<JArray> GetFriendlyFixturesForDateAsync(string apiKey, DateTime utcDate)
    {
        string date = utcDate.ToString("yyyy-MM-dd");

        // Try a few seasons so you do not miss friendlies that are not under the exact current season.
        var seasonsToTry = new[]
        {
            utcDate.Year,
            utcDate.Year - 1,
            utcDate.Year + 1
        };

        foreach (int season in seasonsToTry.Distinct())
        {
            string url =
                $"https://v3.football.api-sports.io/fixtures?date={date}&league={FriendliesLeagueId}&season={season}";

            string responseText = await SendApiRequestAsync(apiKey, url);

            var json = JObject.Parse(responseText);
            var response = json["response"] as JArray ?? new JArray();

            if (response.Count > 0)
                return response;
        }

        return new JArray();
    }

    private async Task<string> SendApiRequestAsync(string apiKey, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-apisports-key", apiKey);

        using var response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            Console.WriteLine($"API HTTP {(int)response.StatusCode}: {body}");

        return body;
    }

    private string ToDiscordRelativeTime(string utcTime)
    {
        if (string.IsNullOrWhiteSpace(utcTime))
            return "?";

        if (!DateTimeOffset.TryParse(utcTime, out var dto))
            return "?";

        long unix = dto.ToUnixTimeSeconds();
        return $"<t:{unix}:R>";
    }

    private string GetFlag(string country)
    {
        if (string.IsNullOrWhiteSpace(country))
            return "";

        country = country.Trim();

        if (country.Length == 2)
            return Iso2ToFlag(country.ToUpperInvariant());

        return CountryToIso2.TryGetValue(country, out string iso2)
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

    private static readonly Dictionary<string, string> CountryToIso2 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Algeria"] = "DZ",
        ["Argentina"] = "AR",
        ["Australia"] = "AU",
        ["Austria"] = "AT",
        ["Belgium"] = "BE",
        ["Bosnia and Herzegovina"] = "BA",
        ["Brazil"] = "BR",
        ["Canada"] = "CA",
        ["Cape Verde"] = "CV",
        ["Colombia"] = "CO",
        ["Croatia"] = "HR",
        ["Curacao"] = "CW",
        ["Curaçao"] = "CW",
        ["Czech Republic"] = "CZ",
        ["Democratic Republic of the Congo"] = "CD",
        ["DR Congo"] = "CD",
        ["Ecuador"] = "EC",
        ["Egypt"] = "EG",
        ["England"] = "GB",
        ["France"] = "FR",
        ["Germany"] = "DE",
        ["Ghana"] = "GH",
        ["Haiti"] = "HT",
        ["Iran"] = "IR",
        ["Iraq"] = "IQ",
        ["Ivory Coast"] = "CI",
        ["Japan"] = "JP",
        ["Jordan"] = "JO",
        ["Mexico"] = "MX",
        ["Morocco"] = "MA",
        ["Netherlands"] = "NL",
        ["New Zealand"] = "NZ",
        ["Norway"] = "NO",
        ["Panama"] = "PA",
        ["Paraguay"] = "PY",
        ["Portugal"] = "PT",
        ["Qatar"] = "QA",
        ["Saudi Arabia"] = "SA",
        ["Scotland"] = "GB",
        ["Senegal"] = "SN",
        ["South Africa"] = "ZA",
        ["South Korea"] = "KR",
        ["Korea Republic"] = "KR",
        ["Spain"] = "ES",
        ["Sweden"] = "SE",
        ["Switzerland"] = "CH",
        ["Tunisia"] = "TN",
        ["Turkey"] = "TR",
        ["United States"] = "US",
        ["USA"] = "US",
        ["Uruguay"] = "UY",
        ["Uzbekistan"] = "UZ"
    };

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