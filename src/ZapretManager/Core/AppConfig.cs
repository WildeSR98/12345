using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZapretManager.Core;

public class AppConfig
{
    [JsonPropertyName("project")]
    public ProjectInfo Project { get; set; } = new();

    [JsonPropertyName("repositories")]
    public Repositories Repositories { get; set; } = new();

    [JsonPropertyName("lists")]
    public ListsConfig Lists { get; set; } = new();

    [JsonPropertyName("diagnostics")]
    public DiagnosticsConfig Diagnostics { get; set; } = new();

    [JsonPropertyName("backup")]
    public BackupConfig Backup { get; set; } = new();

    [JsonPropertyName("features")]
    public FeaturesConfig Features { get; set; } = new();

    public static AppConfig Load(string rootDir)
    {
        var path = Path.Combine(rootDir, "config.json");
        if (!File.Exists(path)) return new AppConfig();
        var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
        return JsonSerializer.Deserialize<AppConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new AppConfig();
    }
}

public class ProjectInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "Zapret Autosetup";
    [JsonPropertyName("version")] public string Version { get; set; } = "2.0.0";
}

public class Repositories
{
    [JsonPropertyName("zapret_core")] public RepoInfo ZapretCore { get; set; } = new();
    [JsonPropertyName("scripts_12345")] public RepoInfo Scripts12345 { get; set; } = new();
}

public class RepoInfo
{
    [JsonPropertyName("owner")] public string Owner { get; set; } = "";
    [JsonPropertyName("repo")] public string Repo { get; set; } = "";
    [JsonPropertyName("release_api")] public string? ReleaseApi { get; set; }
    [JsonPropertyName("version_url")] public string? VersionUrl { get; set; }
    [JsonPropertyName("download_page")] public string? DownloadPage { get; set; }
    [JsonPropertyName("commit_api")] public string? CommitApi { get; set; }
    [JsonPropertyName("archive_url")] public string? ArchiveUrl { get; set; }
    [JsonPropertyName("ipset_service")] public string? IpsetService { get; set; }
    [JsonPropertyName("hosts_service")] public string? HostsService { get; set; }
}

public class ListsConfig
{
    [JsonPropertyName("files")] public List<ListFileEntry> Files { get; set; } = new();
}

public class ListFileEntry
{
    [JsonPropertyName("local")] public string Local { get; set; } = "";
    [JsonPropertyName("remote")] public string Remote { get; set; } = "";
    [JsonPropertyName("stub")] public string Stub { get; set; } = "";
    [JsonPropertyName("user")] public bool User { get; set; }
}

public class DiagnosticsConfig
{
    [JsonPropertyName("check_targets")] public List<CheckTarget> CheckTargets { get; set; } = new();
    [JsonPropertyName("conflicting_services")] public List<string> ConflictingServices { get; set; } = new();
    [JsonPropertyName("dpi_suite_url")] public string DpiSuiteUrl { get; set; } = "";
}

public class CheckTarget
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "url";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
}

public class BackupConfig
{
    [JsonPropertyName("keep_count")] public int KeepCount { get; set; } = 5;
    [JsonPropertyName("include_patterns")] public List<string> IncludePatterns { get; set; } = new();
    [JsonPropertyName("exclude_patterns")] public List<string> ExcludePatterns { get; set; } = new();
}

public class FeaturesConfig
{
    [JsonPropertyName("parallel_downloads")] public bool ParallelDownloads { get; set; } = true;
    [JsonPropertyName("remove_cidr_overlap")] public bool RemoveCidrOverlap { get; set; } = true;
    [JsonPropertyName("verbose_logging")] public bool VerboseLogging { get; set; }
}
