using ReleaseSync;

if (args.Length > 1)
{
    Console.Error.WriteLine("用法：ReleaseSync [配置文件路径]");
    return 1;
}

var configPath = args.Length == 1
    ? Path.GetFullPath(args[0])
    : Path.Combine(AppContext.BaseDirectory, "config.toml");

try
{
    if (!File.Exists(configPath))
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        try
        {
            await using var file = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write);
            await using var writer = new StreamWriter(file);
            await writer.WriteAsync(SyncConfig.Template);
            Console.WriteLine($"未找到配置，已创建模板配置：config.toml。");
        }
        catch (IOException) when (File.Exists(configPath))
        {
            // 另一个进程刚创建了配置文件。
        }
    }

    var configText = await File.ReadAllTextAsync(configPath);
    var config = SyncConfig.Load(configPath, configText);

    using var shutdown = new ShutdownSignal();
    var nextCheck = DateTimeOffset.UtcNow;
    var nextConfigCheck = DateTimeOffset.UtcNow;
    string? rejectedText = null;
    Console.WriteLine($"已加载 {config.Software.Count} 项，每 {config.CheckIntervalMinutes} 分钟检查一次。");
    Console.WriteLine($"下载目录：{config.DestinationRoot}");
    if (!Console.IsInputRedirected)
        Console.WriteLine("按 F5 立即检查，按 Ctrl+C 停止。");

    while (!shutdown.Token.IsCancellationRequested)
    {
        if (ReadRefreshKey())
        {
            nextCheck = DateTimeOffset.UtcNow;
            Console.WriteLine("收到 F5，立即检查 Release。");
        }

        if (DateTimeOffset.UtcNow >= nextCheck)
        {
            if (config.Software.Count > 0)
            {
                using var handler = new HttpClientHandler();
                if (config.CreateProxy() is { } proxy)
                    handler.Proxy = proxy;
                using var http = new HttpClient(handler);
                http.Timeout = Timeout.InfiniteTimeSpan;
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ReleaseSync/1.0");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                if (!string.IsNullOrWhiteSpace(token))
                    http.DefaultRequestHeaders.Authorization = new("Bearer", token);

                await new ReleaseSynchronizer(new GitHubReleaseClient(http), config).RunAsync(shutdown.Token);
            }
            else
                Console.WriteLine("当前无配置源。");

            nextCheck = DateTimeOffset.UtcNow.AddMinutes(config.CheckIntervalMinutes);
            if (config.Software.Count > 0)
                Console.WriteLine($"下一轮检查：{nextCheck.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        }

        if (DateTimeOffset.UtcNow < nextConfigCheck)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), shutdown.Token);
            continue;
        }

        nextConfigCheck = DateTimeOffset.UtcNow.AddSeconds(2);
        try
        {
            var updatedText = await File.ReadAllTextAsync(configPath, shutdown.Token);
            if (updatedText == configText || updatedText == rejectedText)
                continue;

            rejectedText = updatedText;
            var updatedConfig = SyncConfig.Load(configPath, updatedText);
            configText = updatedText;
            config = updatedConfig;
            rejectedText = null;
            nextCheck = DateTimeOffset.UtcNow;
            Console.WriteLine($"配置已更新：{config.Software.Count} 项，每 {config.CheckIntervalMinutes} 分钟检查一次。");
            Console.WriteLine($"下载根目录：{config.DestinationRoot}");
        }
        catch (OperationCanceledException) when (shutdown.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"配置重载失败，继续使用上一份有效配置：{ex.Message}");
        }
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("已停止。");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"启动失败：{ex.Message}");
    return 1;
}

return 0;

static bool ReadRefreshKey()
{
    if (Console.IsInputRedirected)
        return false;

    try
    {
        var refresh = false;
        while (Console.KeyAvailable)
            refresh |= Console.ReadKey(intercept: true).Key == ConsoleKey.F5;
        return refresh;
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException)
    {
        return false;
    }
}

internal sealed class ShutdownSignal : IDisposable
{
    private readonly CancellationTokenSource _source = new();

    public ShutdownSignal() => Console.CancelKeyPress += OnCancel;

    public CancellationToken Token => _source.Token;

    private void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _source.Cancel();
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancel;
        _source.Dispose();
    }
}
