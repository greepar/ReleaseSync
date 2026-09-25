using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LightDl;

namespace ReleaseSync;

internal sealed record SyncedRelease(string Tag, string FileName, string? TargetPath, long Size);

internal sealed class ReleaseSynchronizer(GitHubReleaseClient github, SyncConfig config)
{
    private readonly string _statePath = Path.Combine(config.DestinationRoot, ".releasesync-state.json");

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] 开始检查 Release。");
        Dictionary<string, SyncedRelease> state;
        try
        {
            state = File.Exists(_statePath)
                ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(_statePath, cancellationToken), SyncStateContext.Default.DictionaryStringSyncedRelease)
                  ?? throw new InvalidDataException("同步状态文件为空。")
                : new Dictionary<string, SyncedRelease>(StringComparer.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"无法读取同步状态 {_statePath}：{ex.Message}；本轮跳过，避免覆盖已有文件。", cancellationToken);
            return;
        }

        var claimedTargets = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (var software in config.Software)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SyncOneAsync(software, state, claimedTargets, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[{software.Name}] 同步失败：{ex.Message}", cancellationToken);
            }
        }
    }

    private async Task SyncOneAsync(SoftwareConfig software, Dictionary<string, SyncedRelease> state,
        HashSet<string> claimedTargets, CancellationToken cancellationToken)
    {
        var release = await github.GetLatestAsync(software.Repo, cancellationToken);
        var regex = new Regex("^" + Regex.Escape(software.AssetPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var matches = release.Assets.Where(asset => regex.IsMatch(asset.Name)).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"Release {release.Tag} 中附件匹配数量为 {matches.Length}，预期为 1；模式：{software.AssetPattern}");

        var asset = matches[0];
        if (!SyncConfig.IsFileName(asset.Name))
            throw new InvalidDataException($"附件名称不安全：{asset.Name}");
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"附件下载地址无效：{asset.Url}");
        var fileName = software.FileName ?? asset.Name;
        var directory = Path.GetFullPath(Path.Combine(config.DestinationRoot, software.Destination));
        var target = Path.Combine(directory, fileName);
        if (string.Equals(target, _statePath, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            throw new InvalidDataException($"目标文件不能使用同步状态文件名：{target}");
        if (!claimedTargets.Add(target))
            throw new InvalidDataException($"目标文件被多个软件配置使用：{target}");

        if (state.TryGetValue(software.Name, out var synced) && synced.Tag == release.Tag &&
            synced.FileName == fileName && synced.TargetPath == target &&
            synced.Size == asset.Size && File.Exists(target) && new FileInfo(target).Length == asset.Size)
        {
            Console.WriteLine($"[{software.Name}] 已是最新：{release.Tag}");
            return;
        }

        Directory.CreateDirectory(directory);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(software.Name))[..12]);
        var temp = Path.Combine(directory, $".releasesync-{id}.download");
        var progressState = new DownloadProgressState();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromHours(2));
            using var downloader = new LightDownloader(new LightDownloadConfig
            {
                Proxy = config.CreateProxy(),
                UseProxy = true,
                FileConflictPolicy = LightDownloadFileConflictPolicy.Overwrite
            });
            IReadOnlyDictionary<string, string>? headers = null;
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (!string.IsNullOrWhiteSpace(token) &&
                (url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                 url.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)))
                headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };

            var request = LightDownloadRequest.ToFile(url, temp, headers)
                .OnFileInfo(info => Console.WriteLine($"[{software.Name}] 下载 {asset.Name}（{info.Size} 字节）"))
                .OnProgress(progress =>
                {
                    Interlocked.Exchange(ref progressState.Shown, 1);
                    Console.Write($"\r[{software.Name}] {progress.ProgressPercentage:F1}%  {progress.Speed / 1024 / 1024:F1} MB/s");
                });
            var result = await downloader.DownloadAsync(request, timeout.Token);
            if (Interlocked.Exchange(ref progressState.Shown, 0) != 0)
                Console.WriteLine();

            if (result.Skipped || result.FilePath != temp || result.Size != asset.Size ||
                new FileInfo(temp).Length != asset.Size)
            {
                if (File.Exists(temp)) File.Delete(temp);
                throw new IOException($"下载结果校验失败（预期 {asset.Size} 字节且保存到 {temp}）。");
            }

            File.Move(temp, target, overwrite: true);
            state[software.Name] = new SyncedRelease(release.Tag, fileName, target, asset.Size);
            await SaveStateAsync(state, cancellationToken);
            Console.WriteLine($"[{software.Name}] {release.Tag} → {target}");
        }
        finally
        {
            if (Volatile.Read(ref progressState.Shown) != 0)
                Console.WriteLine();
        }
    }

    private async Task SaveStateAsync(Dictionary<string, SyncedRelease> state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(config.DestinationRoot);
        var temp = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, SyncStateContext.Default.DictionaryStringSyncedRelease), cancellationToken);
            File.Move(temp, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

internal sealed class DownloadProgressState
{
    public int Shown;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, SyncedRelease>))]
internal partial class SyncStateContext : JsonSerializerContext;
