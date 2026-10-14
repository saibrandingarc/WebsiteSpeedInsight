using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using MimeKit;
using System.Data.SqlClient;
using System.Globalization;
using System.Text.Json.Nodes;
using Serilog;
using System.Text;
using System.Text.Json;
using WebsiteSpeedInsight.Services;

namespace WebsiteSpeedInsight
{
    public class WebsiteSpeedInsight
    {
        private readonly IConfiguration config;
        private readonly WpEngineServices _wpEngineServices;

        public class Client
        {
            public int ClientId{get;set;}
            public int ProjectId{get;set;}
            public String? PropertyId{get;set;}
            public string? URL{get;set;}
        }

        public class InputParams{
            public string? url{get;set;}
        }

        public WebsiteSpeedInsight(IConfiguration configuration, WpEngineServices wpEngineServices)
        {
            config = configuration;
            _wpEngineServices = wpEngineServices;
        }

        // Runs once a day at 00:00 UTC. Azure NCRONTAB: {second} {minute} {hour} {day} {month} {day-of-week}
        [Function("WebsiteSpeedInsight")]
        public Task Run([TimerTrigger("0 0 0 * * *")] TimerInfo myTimer)
        {
            Log.Information("WebsiteSpeedInsight function executed at: {Time}", DateTime.UtcNow);
            Console.WriteLine("started website speed insight function");
            Collect();
            if (myTimer.ScheduleStatus is not null)
            {
                Log.Information("Next timer schedule at: {Next}", myTimer.ScheduleStatus.Next);
            }
            return Task.CompletedTask;
        }

        // Runs once a day at 23:00 UTC, before the midnight PageSpeed collection.
        [Function("SyncWpEngineWebsiteData")]
        public async Task SyncWpEngineWebsiteData([TimerTrigger("0 0 23 * * *")] TimerInfo myTimer)
        {
            Log.Information("SyncWpEngineWebsiteData executed at: {Time}", DateTime.UtcNow);
            await SyncWpEngineAsync();
            if (myTimer.ScheduleStatus is not null)
            {
                Log.Information("Next WP Engine sync at: {Next}", myTimer.ScheduleStatus.Next);
            }
        }

        private async Task SyncWpEngineAsync()
        {
            try
            {
                var installs = await _wpEngineServices.GetWebsiteDataAsync();
                EnsureWpEngineWebsiteDataTable();
                foreach (var install in installs)
                {
                    UpsertWpEngineWebsiteData(install);
                }
                Log.Information("wpenginewebsitedata saved {Count} installs", installs.Count);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error : " + e);
                Log.Error(e.Message);
            }
        }

        private void EnsureWpEngineWebsiteDataTable()
        {
            using var connection = new SqlConnection(config["ConnectionStrings:DbConnectionString"]);
            connection.Open();
            using var command = new SqlCommand("""
                IF OBJECT_ID(N'dbo.wpenginewebsitedata', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.wpenginewebsitedata
                    (
                        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_wpenginewebsitedata PRIMARY KEY,
                        SiteId nvarchar(50) NOT NULL,
                        InstallId nvarchar(50) NOT NULL,
                        Name nvarchar(255) NULL,
                        Environment nvarchar(50) NULL,
                        PrimaryDomain nvarchar(255) NULL,
                        CreatedTime datetime NOT NULL,
                        ModifiedTime datetime NULL
                    );

                    CREATE UNIQUE INDEX UX_wpenginewebsitedata_InstallId
                        ON dbo.wpenginewebsitedata (InstallId);
                END

                IF COL_LENGTH('dbo.wpenginewebsitedata', 'Name') IS NULL
                    ALTER TABLE dbo.wpenginewebsitedata ADD Name nvarchar(255) NULL;
                """, connection);
            command.ExecuteNonQuery();
        }

        private void UpsertWpEngineWebsiteData(WpEngineInstall install)
        {
            using var connection = new SqlConnection(config["ConnectionStrings:DbConnectionString"]);
            connection.Open();
            using var command = new SqlCommand("""
                IF EXISTS (SELECT 1 FROM wpenginewebsitedata WHERE InstallId = @InstallId)
                UPDATE wpenginewebsitedata
                SET SiteId = @SiteId,
                    Name = @Name,
                    Environment = @Environment,
                    PrimaryDomain = @PrimaryDomain,
                    ModifiedTime = GETDATE()
                WHERE InstallId = @InstallId
                ELSE
                INSERT INTO wpenginewebsitedata (SiteId, InstallId, Name, Environment, PrimaryDomain, CreatedTime)
                VALUES (@SiteId, @InstallId, @Name, @Environment, @PrimaryDomain, GETDATE())
                """, connection);
            command.Parameters.AddWithValue("@SiteId", install.SiteId ?? "");
            command.Parameters.AddWithValue("@InstallId", install.Id);
            command.Parameters.AddWithValue("@Name", (object?)install.Name ?? DBNull.Value);
            command.Parameters.AddWithValue("@Environment", (object?)install.Environment ?? DBNull.Value);
            command.Parameters.AddWithValue("@PrimaryDomain", (object?)install.PrimaryDomain ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        private void Collect()
        {
            try
            {
                int clientId;
                string? websiteUrl;
                string? installId;
                string? environment;
                int vitalId;
                using (var connection = new SqlConnection(config["ConnectionStrings:DbConnectionString"]))
                {
                    connection.Open();
                    using var command = new SqlCommand("""
                        SELECT TOP 1 w.Id, w.PrimaryDomain, w.InstallId, w.Environment, v.Id
                        FROM wpenginewebsitedata w
                        INNER JOIN WebsiteCoreWebVitals v
                            ON v.ClientId = w.Id
                           AND v.MonthYear = DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)
                        WHERE LOWER(LTRIM(RTRIM(w.Environment))) = 'production'
                          AND w.PrimaryDomain IS NOT NULL
                          AND LTRIM(RTRIM(w.PrimaryDomain)) <> ''
                        ORDER BY ISNULL(v.ModifiedTime, v.CreatedTime), w.Id
                        """, connection);
                    using var reader = command.ExecuteReader();
                    if (!reader.Read())
                    {
                        Log.Information("No saved production website is waiting for a Core Web Vitals update.");
                        return;
                    }

                    clientId = reader.GetInt32(0);
                    websiteUrl = reader.GetString(1);
                    installId = reader.GetString(2);
                    environment = reader.IsDBNull(3) ? null : reader.GetString(3);
                    vitalId = reader.GetInt32(4);
                }

                var pageUrl = websiteUrl.Contains("://", StringComparison.OrdinalIgnoreCase)
                    ? websiteUrl.Trim()
                    : "https://" + websiteUrl.Trim();
                Log.Information("Core Web Vitals for wpengine website {WebsiteId} install {InstallId} url {Url}", clientId, installId, pageUrl);

                var mobile = FetchPageSpeed(pageUrl, "mobile");
                var desktop = FetchPageSpeed(pageUrl, "desktop");
                if (mobile == null || desktop == null)
                {
                    return;
                }

                using var save = new SqlConnection(config["ConnectionStrings:DbConnectionString"]);
                save.Open();
                using var write = new SqlCommand("""
                    UPDATE WebsiteCoreWebVitals
                    SET FCP = @FCP,
                        LCP = @LCP,
                        CLS = @CLS,
                        TBT = @TBT,
                        MobilePerformanceScore = @MobilePerformanceScore,
                        DesktopPerformanceScore = @DesktopPerformanceScore,
                        ModifiedTime = GETDATE()
                    WHERE Id = @Id
                    """, save);
                write.Parameters.AddWithValue("@Id", vitalId);
                write.Parameters.AddWithValue("@FCP", mobile.Fcp);
                write.Parameters.AddWithValue("@LCP", mobile.Lcp);
                write.Parameters.AddWithValue("@CLS", mobile.Cls);
                write.Parameters.AddWithValue("@TBT", mobile.Tbt);
                write.Parameters.AddWithValue("@MobilePerformanceScore", (object?)mobile.PerformanceScore ?? DBNull.Value);
                write.Parameters.AddWithValue("@DesktopPerformanceScore", (object?)desktop.PerformanceScore ?? DBNull.Value);
                write.ExecuteNonQuery();
                Log.Information(
                    "WebsiteCoreWebVitals updated for wpengine website {WebsiteId}. Mobile {MobileScore} Desktop {DesktopScore}",
                    clientId,
                    mobile.PerformanceScore,
                    desktop.PerformanceScore);

                const decimal mobileThreshold = 0.80m;
                const decimal desktopThreshold = 0.85m;
                if (mobile.PerformanceScore < mobileThreshold || desktop.PerformanceScore < desktopThreshold)
                {
                    try
                    {
                        SendLowScoreEmail(pageUrl, environment, mobile, desktop, mobileThreshold, desktopThreshold);
                    }
                    catch (Exception mailError)
                    {
                        Log.Error(mailError, "Low score email failed for {Url}", pageUrl);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error : " + e);
                Log.Error(e.Message);
            }
        }

        private PageSpeedSnapshot? FetchPageSpeed(string pageUrl, string strategy)
        {
            using var httpClient = new HttpClient();
            var apiUrl = "https://www.googleapis.com/pagespeedonline/v5/runPagespeed?url="
                + Uri.EscapeDataString(pageUrl)
                + "&key=" + config["API_Key"]
                + "&strategy=" + strategy
                + "&category=performance";
            using var response = httpClient.GetAsync(apiUrl).Result;
            var json = response.Content.ReadAsStringAsync().Result;
            if (!response.IsSuccessStatusCode)
            {
                Log.Error("PageSpeed {Strategy} request failed with status {Status}", strategy, (int)response.StatusCode);
                return null;
            }

            var jsonObj = JsonNode.Parse(json);
            var audits = jsonObj?["lighthouseResult"]?["audits"];
            decimal? performanceScore = null;
            var scoreNode = jsonObj?["lighthouseResult"]?["categories"]?["performance"]?["score"];
            if (scoreNode is JsonValue scoreValue && scoreValue.TryGetValue(out decimal score))
            {
                performanceScore = score;
            }

            return new PageSpeedSnapshot
            {
                Fcp = audits?["first-contentful-paint"]?["displayValue"]?.ToString() ?? "",
                Lcp = audits?["largest-contentful-paint"]?["displayValue"]?.ToString() ?? "",
                Cls = audits?["cumulative-layout-shift"]?["displayValue"]?.ToString() ?? "",
                Tbt = audits?["total-blocking-time"]?["displayValue"]?.ToString() ?? "",
                PerformanceScore = performanceScore
            };
        }

        private void SendLowScoreEmail(
            string pageUrl,
            string? environment,
            PageSpeedSnapshot mobile,
            PageSpeedSnapshot desktop,
            decimal mobileThreshold,
            decimal desktopThreshold)
        {
            var fromEmail = config["Email:FromEmail"];
            var appPassword = config["Email:AppPassword"];
            var recipients = config.GetSection("Email:ToEmails")
                .GetChildren()
                .Select(child => child.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var singleRecipient = config["Email:ToEmail"];
            if (recipients.Count == 0 && !string.IsNullOrWhiteSpace(singleRecipient))
            {
                recipients.Add(singleRecipient.Trim());
            }
            var host = config["Email:SmtpHost"];
            if (!int.TryParse(config["Email:SmtpPort"], out var port))
            {
                port = 587;
            }

            if (string.IsNullOrWhiteSpace(fromEmail) || string.IsNullOrWhiteSpace(appPassword) || recipients.Count == 0 || string.IsNullOrWhiteSpace(host))
            {
                Log.Error("Low score email was not sent because Email settings are incomplete.");
                return;
            }

            var month = DateTime.UtcNow.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(config["Email:FromName"] ?? "Website Speed Insight", fromEmail));
            foreach (var recipient in recipients)
            {
                message.To.Add(MailboxAddress.Parse(recipient));
            }
            message.Subject = "Website performance below threshold: " + pageUrl;

            var body = new StringBuilder();
            body.AppendLine("A production website is below its PageSpeed threshold.");
            body.AppendLine();
            body.AppendLine("Website: " + pageUrl);
            body.AppendLine("Environment: " + (environment ?? ""));
            body.AppendLine("Month: " + month);
            body.AppendLine("Mobile threshold: 80");
            body.AppendLine("Desktop threshold: 85");
            body.AppendLine();
            body.AppendLine(ScoreLine("Mobile performance", mobile.PerformanceScore, mobileThreshold));
            body.AppendLine(ScoreLine("Desktop performance", desktop.PerformanceScore, desktopThreshold));
            body.AppendLine();
            body.AppendLine("Mobile Core Web Vitals");
            body.AppendLine("FCP: " + mobile.Fcp);
            body.AppendLine("LCP: " + mobile.Lcp);
            body.AppendLine("CLS: " + mobile.Cls);
            body.AppendLine("TBT: " + mobile.Tbt);
            body.AppendLine();
            body.AppendLine("Desktop Core Web Vitals");
            body.AppendLine("FCP: " + desktop.Fcp);
            body.AppendLine("LCP: " + desktop.Lcp);
            body.AppendLine("CLS: " + desktop.Cls);
            body.AppendLine("TBT: " + desktop.Tbt);
            message.Body = new TextPart("plain") { Text = body.ToString() };

            using var smtp = new SmtpClient();
            smtp.CheckCertificateRevocation = false;
            smtp.Connect(host, port, SecureSocketOptions.StartTls);
            smtp.Authenticate(fromEmail, appPassword);
            smtp.Send(message);
            smtp.Disconnect(true);
            Log.Information("Low score email sent to {ToEmail} for {Url}", string.Join(", ", recipients), pageUrl);
        }

        private static string ScoreLine(string label, decimal? score, decimal threshold)
        {
            if (score == null)
            {
                return label + ": not returned";
            }

            var thresholdPercent = (threshold * 100m).ToString("0.##", CultureInfo.InvariantCulture);
            var percent = (score.Value * 100m).ToString("0.##", CultureInfo.InvariantCulture);
            var status = score.Value < threshold ? "below " + thresholdPercent : "meets " + thresholdPercent;
            return label + ": " + percent + " (" + score.Value.ToString("0.####", CultureInfo.InvariantCulture) + ") — " + status;
        }

        private sealed class PageSpeedSnapshot
        {
            public string Fcp { get; init; } = "";
            public string Lcp { get; init; } = "";
            public string Cls { get; init; } = "";
            public string Tbt { get; init; } = "";
            public decimal? PerformanceScore { get; init; }
        }
    }
}
