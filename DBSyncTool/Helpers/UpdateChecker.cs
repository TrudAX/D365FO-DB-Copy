using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace DBSyncTool.Helpers
{
    public class UpdateChecker
    {
        private const string GitHubApiUrl = "https://api.github.com/repos/TrudAX/D365FO-DB-Sync/releases/latest";
        private const string ReleasesPageUrl = "https://github.com/TrudAX/D365FO-DB-Sync/releases";

        public class UpdateCheckResult
        {
            public bool Success { get; set; }
            public bool UpdateAvailable { get; set; }
            public Version? CurrentVersion { get; set; }
            public Version? LatestVersion { get; set; }
            public string? ReleaseUrl { get; set; }
            public string? DownloadUrl { get; set; }
            public string? ReleaseName { get; set; }
            public string? ErrorMessage { get; set; }
        }

        public static Version? GetCurrentVersion()
        {
            return Assembly.GetExecutingAssembly().GetName().Version;
        }

        public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
        {
            var result = new UpdateCheckResult
            {
                CurrentVersion = GetCurrentVersion()
            };

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "D365FO-DB-Sync");
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
                client.Timeout = TimeSpan.FromSeconds(10);

                var response = await client.GetAsync(GitHubApiUrl);

                if (!response.IsSuccessStatusCode)
                {
                    result.Success = false;
                    result.ErrorMessage = $"GitHub API returned {response.StatusCode}";
                    return result;
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var tagName = root.GetProperty("tag_name").GetString();
                var latestVersion = ParseVersionFromTag(tagName);

                if (latestVersion == null)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Could not parse version from tag: {tagName}";
                    return result;
                }

                result.Success = true;
                result.LatestVersion = latestVersion;
                result.ReleaseName = root.GetProperty("name").GetString();
                result.ReleaseUrl = root.GetProperty("html_url").GetString();

                // Try to get download URL for exe
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var assetName = asset.GetProperty("name").GetString();
                        if (assetName != null && assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            result.DownloadUrl = asset.GetProperty("browser_download_url").GetString();
                            break;
                        }
                    }
                }

                result.UpdateAvailable = latestVersion > result.CurrentVersion;

                return result;
            }
            catch (TaskCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "Connection timed out. Please check your internet connection.";
                return result;
            }
            catch (HttpRequestException ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Network error: {ex.Message}";
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Error checking for updates: {ex.Message}";
                return result;
            }
        }

        private static Version? ParseVersionFromTag(string? tag)
        {
            if (string.IsNullOrEmpty(tag))
                return null;

            var versionString = tag.TrimStart('v', 'V');

            if (Version.TryParse(versionString, out var version))
                return version;

            return null;
        }

        public static string GetReleasesPageUrl() => ReleasesPageUrl;
    }
}
