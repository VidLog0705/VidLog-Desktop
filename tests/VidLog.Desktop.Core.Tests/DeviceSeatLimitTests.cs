using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 许可的机位闸门接在**入网**那条路上（`docs/04-许可设计.md` §5.1 / 规格 §3.9）。
/// </summary>
/// <remarks>
/// <para>
/// <b>它只做一件事：拒绝新的手机接进来。</b>所以这里的断言分两半 ——
/// 「超限接不进来」和「已经进来的照常」。
/// </para>
/// <para>
/// ⚠️ <b>「0 机位」是这条规则的一个例外</b>（2026-10-03）：0 机位的意思是
/// **这台机器没有许可**（未激活 / 试用已结束），不是「坐满了」——
/// 所以连已经在册的那台也不放行，有一条专门钉它。
/// </para>
/// <para>
/// ⚠️ <b>L8 红线不在这个文件里守</b>：许可不得锁住已有录像（检索 / 回放 / 导出
/// 一条都不看许可）由 <c>LicenseIndependenceTests</c> 守着。这里只保证
/// 机位闸门没有顺手把别的路也堵上 —— 所以有一条专门验「已入网的设备还能换码重配」。
/// </para>
/// </remarks>
public class DeviceSeatLimitTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-seat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            // ⚠️ **宽着接**：Windows 在「文件正被另一个进程持有」时可能报
            // `UnauthorizedAccessException` 而不是 `IOException`（2026-09-27 实测：会话还在
            // 收尾时删含 `.ass` / `.mkv` 的临时目录）。清理失败不该让任何一条测试红。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 走完一台设备的入网：报到 → 批准 → 领凭据。
    /// </summary>
    /// <remarks>
    /// ⚠️ 报到那一步就被挡下时**直接返回那一步的结果**，不再往下走 ——
    /// 机位闸门就在那一步上（见 <see cref="DeviceRegistry.SeatBlockedAsync"/>），
    /// 继续走下去只会把「机位满了」变成一句「没有待批准的请求」，
    /// 那是另一个原因，会把这里的断言骗过去。
    /// </remarks>
    private static async Task<ClaimResult> EnrollAsync(DeviceRegistry registry, string deviceId)
    {
        var session = await registry.OpenSessionAsync();
        var request = await registry.RequestAsync(deviceId, $"打包手机-{deviceId}", session.Token);

        if (request.Status != EnrollStatus.Pending)
        {
            return request;
        }

        await registry.DecideAsync(deviceId, approved: true);

        return await registry.ClaimAsync(deviceId, session.Token);
    }

    [Fact]
    public async Task 机位没满时照常接得进来()
    {
        using var dir = new TempDir();
        var registry = new DeviceRegistry(dir.File("devices.jsonl"), seatLimit: () => 2);

        Assert.Equal(EnrollStatus.Approved, (await EnrollAsync(registry, "phone-1")).Status);
        Assert.Equal(EnrollStatus.Approved, (await EnrollAsync(registry, "phone-2")).Status);
    }

    [Fact]
    public async Task 机位满了第三台接不进来()
    {
        // 2 机位的码接了 2 台之后，第三台必须被挡住 —— 这是「许可只限机位」的全部牙齿。
        using var dir = new TempDir();
        var registry = new DeviceRegistry(dir.File("devices.jsonl"), seatLimit: () => 2);

        await EnrollAsync(registry, "phone-1");
        await EnrollAsync(registry, "phone-2");

        var third = await EnrollAsync(registry, "phone-3");

        Assert.Equal(EnrollStatus.SeatLimitExceeded, third.Status);
        Assert.Null(third.Credential);
        Assert.NotNull(third.Detail);
    }

    [Fact]
    public async Task 机位归零后_已经在册的那台也不许重新入网()
    {
        // ⚠️ 「档位用满」与「0 机位」是两回事（2026-10-03 用户裁定：
        //    「7 天试用期结束，如未重新激活，则电脑端功能均不能使用」）。
        //    前者可以放行已经在册的那台（它本来就占着位置），后者一个机位都没有，
        //    没有「本来就占着」可言。
        //
        // 先给 4 个机位把手机接进来，再拨成 0 —— 那就是「重启之后校验发现试用已结束」
        // 的样子（许可在一次运行里是冻结的，L7）。
        using var dir = new TempDir();

        var seats = new[] { 4 };
        var registry = new DeviceRegistry(dir.File("devices.jsonl"), seatLimit: () => seats[0]);

        Assert.Equal(EnrollStatus.Approved, (await EnrollAsync(registry, "phone-1")).Status);

        seats[0] = 0;

        var again = await EnrollAsync(registry, "phone-1");

        Assert.Equal(EnrollStatus.SeatLimitExceeded, again.Status);
        Assert.Null(again.Credential);

        // ⚠️ 说的必须是「没激活」，不是「满了」—— 两句都是实话，但指向两个不同的动作。
        Assert.Contains("激活", again.Detail);
    }

    [Fact]
    public async Task 未激活一台都接不进来()
    {
        // 机位数 0 = 没激活（或者软件没配好公钥）。这个档位**一台都不给** ——
        // 许可的全部意义就是「允许接几台手机」，没有激活就没有机位。
        using var dir = new TempDir();
        var registry = new DeviceRegistry(dir.File("devices.jsonl"), seatLimit: () => 0);

        var first = await EnrollAsync(registry, "phone-1");

        Assert.Equal(EnrollStatus.SeatLimitExceeded, first.Status);
        Assert.Null(first.Credential);
    }

    [Fact]
    public async Task 超限的那条请求不会出现在电脑端的待批准列表里()
    {
        // 让用户白点一次「同意」是小事，但**更糟的是**：他点了同意，
        // 屏幕上却什么都没发生（凭据没发出去）。所以超限的请求压根不记。
        using var dir = new TempDir();
        var registry = new DeviceRegistry(dir.File("devices.jsonl"), seatLimit: () => 1);

        await EnrollAsync(registry, "phone-1");

        var session = await registry.OpenSessionAsync();
        var blocked = await registry.RequestAsync("phone-2", "打包手机-2", session.Token);

        Assert.Equal(EnrollStatus.SeatLimitExceeded, blocked.Status);
        Assert.Empty(await registry.PendingAsync());
    }

    [Fact]
    public async Task 已经在录的手机不受影响_只是不能重新入网占新机位()
    {
        // ⚠️ L5 / L8：许可**只挡新的接入**。已经接进来的那台手机丢了凭据、
        // 要重新扫一次码 —— 它占的是**已有的**那个机位，不是新的一台，
        // 所以即使档位已经用满也必须放行。
        //
        // 不放行的话，「机位满了之后手机丢了」会变成死局：用户手里那个激活码明明是够的。
        //
        // ⚠️ 这一条说的是**档位用满**（1 机位），不是 0 机位 ——
        // 0 机位那条例外在上一条里钉着。
        using var dir = new TempDir();
        var registry = new DeviceRegistry(dir.File("devices.jsonl"), seatLimit: () => 1);

        await EnrollAsync(registry, "phone-1");

        // 同一台设备重新走一遍入网 —— 机位是满的，但它本来就占着那个位置。
        var again = await EnrollAsync(registry, "phone-1");

        Assert.Equal(EnrollStatus.Approved, again.Status);
        Assert.NotNull(again.Credential);

        // 而且**没有多占一个**：再来的第二台照样被挡。
        Assert.Equal(
            EnrollStatus.SeatLimitExceeded,
            (await EnrollAsync(registry, "phone-2")).Status);
    }

    [Fact]
    public async Task 一张码只服务一次入网_所以超限的设备走不到签发那一步()
    {
        // ⚠️ 这一条钉的是「**为什么机位闸门只在 request 那一处就够了**」
        // （见 `DeviceRegistry.SeatBlockedAsync` 的说明）：
        // 两台手机同时报上来时两条请求都记下了，但先被批准的那台领走凭据时
        // 整张码就作废了 —— 另一台去领拿到的是「没有待批准的请求」，
        // **根本走不到签发凭据那一步**。所以不存在「批准与领取之间被插队」的口子，
        // 也就没有第二道闸门要守。
        //
        // 将来真要让一张码服务多台设备，这条会红 —— 那时必须把机位闸门搬到
        // `ClaimAsync` 去。这条测试就是那个提醒。
        using var dir = new TempDir();
        var registry = new DeviceRegistry(dir.File("devices.jsonl"), seatLimit: () => 1);

        var session = await registry.OpenSessionAsync();
        await registry.RequestAsync("phone-1", "打包手机-1", session.Token);
        await registry.RequestAsync("phone-2", "打包手机-2", session.Token);

        await registry.DecideAsync("phone-2", approved: true);
        Assert.Equal(
            EnrollStatus.Approved,
            (await registry.ClaimAsync("phone-2", session.Token)).Status);

        // phone-1 也批了、也拿着**同一张码**的令牌，但那张码已经作废。
        await registry.DecideAsync("phone-1", approved: true);
        var late = await registry.ClaimAsync("phone-1", session.Token);

        Assert.Equal(EnrollStatus.NoPendingRequest, late.Status);
        Assert.Null(late.Credential);
    }

    [Fact]
    public async Task 不传机位数就是不限_那是测试专用的口子()
    {
        // 生产路径在 `DesktopServices` 里**一定**接上许可的档位。
        // null = 不限只服务于「这些测试测的不是机位」的那些用例（见 UploadReceiverTests）。
        using var dir = new TempDir();
        var registry = new DeviceRegistry(dir.File("devices.jsonl"));

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(EnrollStatus.Approved, (await EnrollAsync(registry, $"phone-{i}")).Status);
        }
    }
}
