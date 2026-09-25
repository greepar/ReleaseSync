using System.Text.Json;

namespace ReleaseSync;

internal sealed record ReleaseAsset(string Name, string Url, long Size);
internal sealed record GitHubRelease(string Tag, IReadOnlyList<ReleaseAsset> Assets);

internal sealed class GitHubReleaseClient(HttpClient http)
{
    public async Task<GitHubRelease> GetLatestAsync(string repo, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidDataException($"{repo}: Release 没有 tag_name。");

        var assets = new List<ReleaseAsset>();
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            assets.Add(new(
                asset.GetProperty("name").GetString() ?? "",
                asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.GetProperty("size").GetInt64()));
        }
        return new(tag, assets);
    }
}
