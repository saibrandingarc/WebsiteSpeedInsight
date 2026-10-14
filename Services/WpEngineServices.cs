using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace WebsiteSpeedInsight.Services
{
    public sealed class WpEngineAccount
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
    }

    public sealed class WpEngineSite
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public string? AccountId { get; set; }
    }

    public sealed class WpEngineInstall
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public string? Cname { get; set; }
        public string? PrimaryDomain { get; set; }
        public string? Environment { get; set; }
        public string? Status { get; set; }
        public string? SiteId { get; set; }
        public string? AccountId { get; set; }
    }

    public class WpEngineServices
    {
        private const string DefaultBaseUrl = "https://api.wpengineapi.com/v1";
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;

        public WpEngineServices(IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
        }

        public Task<IReadOnlyList<WpEngineAccount>> GetAccountsAsync()
        {
            return GetPagedAsync("accounts", item => new WpEngineAccount
            {
                Id = ReadText(item["id"]) ?? "",
                Name = ReadText(item["name"])
            });
        }

        public Task<IReadOnlyList<WpEngineSite>> GetSitesAsync()
        {
            return GetPagedAsync("sites", item => new WpEngineSite
            {
                Id = ReadText(item["id"]) ?? "",
                Name = ReadText(item["name"]),
                AccountId = LookupId(item["account"])
            });
        }

        public Task<IReadOnlyList<WpEngineInstall>> GetInstallsAsync()
        {
            return GetPagedAsync("installs", item => new WpEngineInstall
            {
                Id = ReadText(item["id"]) ?? "",
                Name = ReadText(item["name"]),
                Cname = ReadText(item["cname"]),
                PrimaryDomain = ReadText(item["primary_domain"]),
                Environment = ReadText(item["environment"]),
                Status = ReadText(item["status"]),
                SiteId = LookupId(item["site"]),
                AccountId = LookupId(item["account"])
            });
        }

        public async Task<IReadOnlyList<WpEngineInstall>> GetWebsiteDataAsync()
        {
            var httpClient = CreateClient();
            if (httpClient == null)
            {
                return Array.Empty<WpEngineInstall>();
            }

            var accountIds = GetAccountIds();
            if (accountIds.Count == 0)
            {
                Log.Error("WP Engine AccountIds are not configured.");
                return Array.Empty<WpEngineInstall>();
            }

            var baseUrl = (_configuration["WpEngine:BaseUrl"] ?? DefaultBaseUrl).TrimEnd('/');
            var sites = new List<JsonObject>();
            foreach (var accountId in accountIds)
            {
                string? nextUrl = baseUrl + "/sites?account_id=" + Uri.EscapeDataString(accountId) + "&limit=100";
                for (var page = 0; page < 50 && !string.IsNullOrWhiteSpace(nextUrl); page++)
                {
                    var root = await GetJsonAsync(httpClient, nextUrl);
                    if (root?["results"] is JsonArray results)
                    {
                        foreach (var item in results)
                        {
                            if (item is JsonObject site)
                            {
                                sites.Add(site);
                            }
                        }
                    }

                    nextUrl = ReadText(root?["next"]);
                }
            }

            var installs = new List<WpEngineInstall>();
            foreach (var site in sites)
            {
                var siteId = ReadText(site["id"]);
                if (site["installs"] is not JsonArray installList)
                {
                    continue;
                }

                foreach (var item in installList)
                {
                    if (item is not JsonObject summary)
                    {
                        continue;
                    }

                    var installId = ReadText(summary["id"]);
                    if (string.IsNullOrWhiteSpace(installId) || string.IsNullOrWhiteSpace(siteId))
                    {
                        continue;
                    }

                    var detail = await GetJsonAsync(httpClient, baseUrl + "/installs/" + Uri.EscapeDataString(installId));
                    if (detail == null)
                    {
                        continue;
                    }

                    installs.Add(new WpEngineInstall
                    {
                        Id = installId,
                        SiteId = siteId,
                        Name = ReadText(detail["name"]) ?? ReadText(summary["name"]),
                        Cname = ReadText(detail["cname"]) ?? ReadText(summary["cname"]),
                        PrimaryDomain = ReadText(detail["primary_domain"]),
                        Environment = ReadText(detail["environment"]) ?? ReadText(summary["environment"]),
                        Status = ReadText(detail["status"]),
                        AccountId = LookupId(detail["account"])
                    });
                }
            }

            return installs;
        }

        private async Task<IReadOnlyList<T>> GetPagedAsync<T>(string resource, Func<JsonObject, T> map)
        {
            var httpClient = CreateClient();
            if (httpClient == null)
            {
                return Array.Empty<T>();
            }

            var results = new List<T>();
            var baseUrl = (_configuration["WpEngine:BaseUrl"] ?? DefaultBaseUrl).TrimEnd('/');
            string? nextUrl = baseUrl + "/" + resource + "?limit=100&offset=0";

            for (var page = 0; page < 50 && !string.IsNullOrWhiteSpace(nextUrl); page++)
            {
                using var response = await httpClient.GetAsync(nextUrl);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
                {
                    Log.Error("WP Engine {Resource} request failed with status {Status}", resource, (int)response.StatusCode);
                    break;
                }

                var root = JsonNode.Parse(body) as JsonObject;
                if (root?["results"] is JsonArray items)
                {
                    foreach (var item in items)
                    {
                        if (item is JsonObject record)
                        {
                            var mapped = map(record);
                            results.Add(mapped);
                        }
                    }
                }

                nextUrl = ReadText(root?["next"]);
            }

            return results;
        }

        private async Task<JsonObject?> GetJsonAsync(HttpClient httpClient, string url)
        {
            using var response = await httpClient.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
            {
                Log.Error("WP Engine request failed with status {Status} for {Url}", (int)response.StatusCode, url);
                return null;
            }

            return JsonNode.Parse(body) as JsonObject;
        }

        private List<string> GetAccountIds()
        {
            var ids = _configuration.GetSection("WpEngine:AccountIds")
                .GetChildren()
                .Select(child => child.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var single = _configuration["WpEngine:AccountId"];
            if (ids.Count == 0 && !string.IsNullOrWhiteSpace(single))
            {
                ids.Add(single.Trim());
            }

            return ids;
        }

        private HttpClient? CreateClient()
        {
            var apiUser = _configuration["WpEngine:ApiUser"];
            var apiPassword = _configuration["WpEngine:ApiPassword"];
            if (string.IsNullOrWhiteSpace(apiUser) || string.IsNullOrWhiteSpace(apiPassword))
            {
                Log.Warning("WP Engine API credentials are not configured.");
                return null;
            }

            var client = _httpClientFactory.CreateClient();
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes(apiUser + ":" + apiPassword));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        private static string? LookupId(JsonNode? node)
        {
            return node is JsonObject obj ? ReadText(obj["id"]) : ReadText(node);
        }

        private static string? ReadText(JsonNode? node)
        {
            if (node is null)
            {
                return null;
            }

            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }

            var raw = node.ToString();
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().Trim('"');
        }
    }
}
