using System.Net;
using Tomlyn;
using Tomlyn.Serialization;
using System.Text.Json.Serialization;

namespace ReleaseSync;

internal sealed record SoftwareConfig(string Name, string Repo, string AssetPattern, string Destination, string? FileName);

internal sealed record SyncConfig(int CheckIntervalMinutes, string DestinationRoot, Uri? ProxyUri,
    IReadOnlyList<SoftwareConfig> Software)
{
    public const string Template = """
        # ReleaseSync 配置；启动时检查一次，之后按间隔检查，保存更改会立即重查。
        # 默认下载到程序所在目录的 release 文件夹。
        # 自定义 destination_root 时，相对路径以此配置文件所在目录为基准。
        # destination_root = "./release"
        check_interval_minutes = 1440 # 例如 15 表示每 15 分钟；1440 表示每天
        # proxy = "http://127.0.0.1:7890" # 可选；也支持 https:// 代理

        # 每个软件添加一组 [[software]]；未添加时程序会等待配置更新。
        # asset_pattern 按附件文件名匹配，支持 * 和 ?，必须恰好匹配一个附件。
        # [[software]]
        # name = "example"
        # repo = "owner/repository"
        # asset_pattern = "example-*-linux-x64.tar.gz"
        # destination = "example" # 相对于 destination_root；填写 "." 表示根目录
        # file_name = "example.tar.gz" # 可选；不写则沿用附件原名
        """;

    public static SyncConfig Load(string configPath, string text)
    {
        var table = TomlSerializer.Deserialize(text, SyncTomlContext.Default.TomlConfig)
                    ?? throw new InvalidDataException("配置文件为空。");
        if (table.CheckIntervalMinutes is not null && table.CheckIntervalHours is not null)
            throw new InvalidDataException("check_interval_minutes 和 check_interval_hours 只能设置一个。");

        long minutes;
        if (table.CheckIntervalHours is { } hours)
        {
            if (hours is < 1 or > 168)
                throw new InvalidDataException("check_interval_hours 必须在 1 到 168 之间。");
            minutes = hours * 60;
        }
        else
        {
            minutes = table.CheckIntervalMinutes ?? 1440;
        }

        if (minutes is < 1 or > 10080)
            throw new InvalidDataException("check_interval_minutes 必须在 1 到 10080 之间。");

        var root = table.DestinationRoot is null
            ? Path.Combine(AppContext.BaseDirectory, "release")
            : RequiredString(table.DestinationRoot, "destination_root");
        if (!Path.IsPathFullyQualified(root))
            root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, root));

        Uri? proxyUri = null;
        if (table.Proxy is not null)
        {
            if (!Uri.TryCreate(table.Proxy, UriKind.Absolute, out proxyUri) ||
                proxyUri.Scheme is not ("http" or "https") ||
                string.IsNullOrEmpty(proxyUri.Host) ||
                proxyUri.AbsolutePath != "/" || proxyUri.Query.Length > 0 || proxyUri.Fragment.Length > 0)
                throw new InvalidDataException("proxy 必须是 http:// 或 https:// 开头的代理地址，例如 http://127.0.0.1:7890。");
        }

        var array = table.Software ?? [];

        var software = new List<SoftwareConfig>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            var name = RequiredString(item.Name, "name");
            var repo = RequiredString(item.Repo, "repo");
            var pattern = RequiredString(item.AssetPattern, "asset_pattern");
            var destination = RequiredString(item.Destination, "destination");
            var fileName = item.FileName;

            if (!names.Add(name))
                throw new InvalidDataException($"软件名称重复：{name}");
            var parts = repo.Split('/');
            if (parts.Length != 2 || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')))
                throw new InvalidDataException($"{name}: repo 应为 owner/repository。");
            if (pattern.Contains('/') || pattern.Contains('\\'))
                throw new InvalidDataException($"{name}: asset_pattern 只能匹配附件文件名。");
            if (Path.IsPathRooted(destination) ||
                destination.Split('/', '\\').Any(part => part == "..") ||
                destination.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new InvalidDataException($"{name}: destination 必须是根目录内的相对子目录。");
            if (fileName is not null && (string.IsNullOrWhiteSpace(fileName) || !IsFileName(fileName)))
                throw new InvalidDataException($"{name}: file_name 必须是单个文件名。");

            software.Add(new(name, repo, pattern, destination, fileName));
        }

        return new((int)minutes, Path.GetFullPath(root), proxyUri, software);
    }

    public WebProxy? CreateProxy()
    {
        if (ProxyUri is null)
            return null;

        var proxy = new WebProxy(ProxyUri);
        if (!string.IsNullOrEmpty(ProxyUri.UserInfo))
        {
            var parts = ProxyUri.UserInfo.Split(':', 2);
            proxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "");
        }

        return proxy;
    }

    public static bool IsFileName(string value) =>
        value is not "" and not "." and not ".." &&
        value == Path.GetFileName(value) &&
        !value.Contains('/') && !value.Contains('\\') &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string RequiredString(string? value, string key) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"缺少有效的字符串配置：{key}");
}

internal sealed class TomlConfig
{
    [JsonPropertyName("check_interval_minutes")]
    public long? CheckIntervalMinutes { get; set; }

    [JsonPropertyName("check_interval_hours")]
    public long? CheckIntervalHours { get; set; }

    [JsonPropertyName("destination_root")]
    public string? DestinationRoot { get; set; }

    [JsonPropertyName("proxy")]
    public string? Proxy { get; set; }

    [JsonPropertyName("software")]
    public List<TomlSoftware>? Software { get; set; }
}

internal sealed class TomlSoftware
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("repo")]
    public string? Repo { get; set; }

    [JsonPropertyName("asset_pattern")]
    public string? AssetPattern { get; set; }

    [JsonPropertyName("destination")]
    public string? Destination { get; set; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }
}

[TomlSerializable(typeof(TomlConfig))]
internal partial class SyncTomlContext : TomlSerializerContext
{
    internal SyncTomlContext()
    {
    }
}
