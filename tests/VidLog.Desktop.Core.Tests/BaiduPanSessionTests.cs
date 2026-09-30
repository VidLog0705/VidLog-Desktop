using VidLog.Desktop.Core.Cloud;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 令牌的生命周期：登录、续期、退出（批次 5）。
/// </summary>
/// <remarks>
/// 这一批里最贵的一个 bug 是**续期撞车**：后台的自动补传与用户手点的
/// 「立即对比同步」会同时发现令牌过期，两个刷新请求一起发出去时，
/// 后一个会把前一个刚拿到的令牌作废掉（网盘的刷新令牌是一次性的那种）——
/// 表现是**随机地掉线**，而日志上什么都看不出来。
/// </remarks>
public class BaiduPanSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task 还没过期就不续期()
    {
        using var dir = new TempDir();
        var api = new FakeApi();
        var session = new BaiduPanSession(api, new BaiduPanTokenStore(dir.TokenPath), now: () => Now);

        await SeedAsync(session, dir, Now.AddHours(1));

        Assert.Equal("access-1", await session.TokenAsync());
        Assert.Equal(0, api.Refreshes);
    }

    [Fact]
    public async Task 快到点了就提前续期()
    {
        // 阈值是 5 分钟，不是「已经过期」。一次上传可能跑十几分钟 ——
        // 用「还剩 30 秒」当阈值的话，一批传到一半令牌就过期了，整条队列全失败。
        using var dir = new TempDir();
        var api = new FakeApi();
        var session = new BaiduPanSession(api, new BaiduPanTokenStore(dir.TokenPath), now: () => Now);

        await SeedAsync(session, dir, Now.AddMinutes(4));

        Assert.Equal("access-2", await session.TokenAsync());
        Assert.Equal(1, api.Refreshes);
    }

    [Fact]
    public async Task 网盘不回新的刷新令牌时沿用旧的那个()
    {
        // ⚠️ 网盘**有时不回新的 refresh_token**。拿空串去覆盖会让下一次续期
        // 彻底失败 —— 而那时用户看到的是「莫名其妙要重新登录」。
        using var dir = new TempDir();
        var api = new FakeApi { RefreshReturnsEmptyRefreshToken = true };
        var session = new BaiduPanSession(api, new BaiduPanTokenStore(dir.TokenPath), now: () => Now);

        await SeedAsync(session, dir, Now.AddMinutes(1));

        Assert.Equal("access-2", await session.TokenAsync());
        Assert.Equal("refresh-1", session.Login!.RefreshToken);

        // 而且**落盘的那一份**也必须是旧的（不然重启之后照样掉线）。
        var reloaded = await new BaiduPanTokenStore(dir.TokenPath).LoadAsync();
        Assert.Equal("refresh-1", reloaded!.RefreshToken);
    }

    [Fact]
    public async Task 两个并发的取令牌只续期一次()
    {
        // 判据是「网盘那边收到几次续期请求」，所以这里数的是 api 的调用次数。
        using var dir = new TempDir();
        var api = new FakeApi { RefreshDelay = TimeSpan.FromMilliseconds(30) };
        var session = new BaiduPanSession(api, new BaiduPanTokenStore(dir.TokenPath), now: () => Now);

        await SeedAsync(session, dir, Now.AddSeconds(10));

        var tokens = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Task.Run(() => session.TokenAsync())));

        Assert.Equal(1, api.Refreshes);
        Assert.All(tokens, t => Assert.Equal("access-2", t));
    }

    [Fact]
    public async Task 没登录时取令牌抛的是人话()
    {
        using var dir = new TempDir();
        var session = new BaiduPanSession(
            new FakeApi(), new BaiduPanTokenStore(dir.TokenPath), now: () => Now);

        var error = await Assert.ThrowsAsync<BaiduPanException>(() => session.TokenAsync());
        Assert.Contains("还没登录", error.Message);
    }

    [Fact]
    public async Task 退出登录只删本机令牌()
    {
        using var dir = new TempDir();
        var session = new BaiduPanSession(
            new FakeApi(), new BaiduPanTokenStore(dir.TokenPath), now: () => Now);

        await SeedAsync(session, dir, Now.AddHours(1));
        Assert.True(session.IsLoggedIn);

        await session.LogoutAsync();

        Assert.False(session.IsLoggedIn);
        Assert.Null(session.Login);

        // 「退出登录」= 忘掉本机的登录信息。**网盘上的文件一个都没动** ——
        // 这里能验的是「本机那份没了」，网盘那边本来就没碰（接口里没有删文件的调用）。
        Assert.False(File.Exists(dir.TokenPath));
    }

    [Fact]
    public async Task 令牌文件读坏了按没登录处理_不抛()
    {
        // 一个坏掉的令牌文件不该让整个程序起不来，也不该让「百度网盘」那一页
        // 变成一片红 —— 重新登录一次就好。但要留痕。
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.TokenPath, "{这不是 JSON");

        var logger = new CapturingLogger();
        var session = new BaiduPanSession(
            new FakeApi(), new BaiduPanTokenStore(dir.TokenPath, logger), now: () => Now);

        Assert.Null(await session.RestoreAsync());
        Assert.False(session.IsLoggedIn);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warn);
    }

    private static async Task SeedAsync(BaiduPanSession session, TempDir dir, DateTimeOffset expiresAt)
    {
        await new BaiduPanTokenStore(dir.TokenPath)
            .SaveAsync(new BaiduLogin("access-1", "refresh-1", expiresAt, "测试账号"));

        await session.RestoreAsync();
    }

    /// <summary>假网盘：只做「翻译」，与真实现同一个约定。</summary>
    private sealed class FakeApi : IBaiduPanApi
    {
        public int Refreshes { get; private set; }

        public TimeSpan RefreshDelay { get; init; }

        public bool RefreshReturnsEmptyRefreshToken { get; init; }

        public Task<BaiduDeviceCode> StartDeviceLoginAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BaiduDeviceCode("dev-1", "USER01", "https://example.invalid/d", "", 5, 300));

        public Task<BaiduToken?> PollDeviceTokenAsync(
            string deviceCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<BaiduToken?>(null);

        public async Task<BaiduToken> RefreshAsync(
            string refreshToken, CancellationToken cancellationToken = default)
        {
            Refreshes++;

            if (RefreshDelay > TimeSpan.Zero)
            {
                await Task.Delay(RefreshDelay, cancellationToken);
            }

            return new BaiduToken(
                "access-2",
                RefreshReturnsEmptyRefreshToken ? string.Empty : "refresh-2",
                3600);
        }

        public Task<string> GetDisplayNameAsync(
            string accessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult("测试账号");

        public Task<BaiduPrecreate> PrecreateAsync(
            string accessToken,
            string remotePath,
            long size,
            IReadOnlyList<string> blockList,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new BaiduPrecreate(
                "upload-1", new HashSet<int>(Enumerable.Range(0, blockList.Count))));

        public Task CreateDirectoryAsync(
            string accessToken, string directory, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> LocateUploadAsync(
            string accessToken,
            string remotePath,
            string uploadId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("https://c3.pcs.baidu.com");

        public Task CreateAsync(
            string accessToken,
            string remotePath,
            long size,
            IReadOnlyList<string> blockList,
            string uploadId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string?> UploadSliceAsync(
            string accessToken,
            string uploadHost,
            string remotePath,
            string uploadId,
            int partSeq,
            Stream content,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<IReadOnlySet<string>> ListFilesAsync(
            string accessToken, string directory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed class CapturingLogger : IAppLogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void Log(LogLevel level, string category, string message) =>
            Entries.Add((level, message));

        public void Log(
            LogLevel level, string category, string message, IReadOnlyDictionary<string, object?> data) =>
            Entries.Add((level, message));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vidlog-pan-session-" + Guid.NewGuid().ToString("N"));

        public string TokenPath => System.IO.Path.Combine(Path, "baidu-pan-token.json");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
