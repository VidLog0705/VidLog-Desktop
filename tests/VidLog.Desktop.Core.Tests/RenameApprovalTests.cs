using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 改名要电脑端同意（规格 §3.4.5 ③）。
/// </summary>
/// <remarks>
/// <para>
/// 需求方 2026-09-24 原话：「扫码连接电脑端，并且电脑端同意连接后，手机端提示连接成功，
/// 请编辑机位名 12 个字符内，用户编写确认后，电脑端显示手机端的名字，并且以后该手机端
/// 连接电脑端都是这个名字，<b>如需要再次更改，需要电脑端同意才能更改</b>。」
/// </para>
/// <para>
/// ⚠️ 在本次之前**没有任何改名通路**：手机端 `rename()` 只改本机（`device_identity.dart`），
/// 电脑端要等下次报到才看到新名字 —— 而「需要电脑端同意」这条**根本不存在**。
/// </para>
/// </remarks>
public class RenameApprovalTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-rename-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            // 宽着接（与其它测试文件同一个口径）。
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 走完一台设备的入网，返回它的凭据。
    /// </summary>
    /// <remarks>
    /// ⚠️ 名字用 <c>机位-phone-1</c>（**正好 12 格**）：这条路上名字会被
    /// `DeviceNameRules.Clamp` 量过（§53 加的），随手写个长名字的话
    /// 下面「名字没变」那些断言会拿到**截断过的**值。
    /// （第一版写的 `打包手机-phone-1` 是 15 格，实测被截成 `打包手机-pho`
    /// —— 测试红了，而错的是夹具不是实现。）
    /// </remarks>
    private static async Task<string> EnrollAsync(DeviceRegistry registry, string deviceId)
    {
        var session = await registry.OpenSessionAsync();
        await registry.RequestAsync(deviceId, $"机位-{deviceId}", session.Token);
        await registry.DecideAsync(deviceId, approved: true);

        var claim = await registry.ClaimAsync(deviceId, session.Token);
        Assert.Equal(EnrollStatus.Approved, claim.Status);

        return claim.Credential!;
    }

    [Fact]
    public async Task 凭据对不上就改不了_不能改别人的名字()
    {
        // ⚠️ 这是这条路的**安全边界**：报文里根本没有 deviceId（见
        // `RenameRequestPayload` 的说明），身份来自凭据反查。
        // 凭据对不上 ⇒ 什么都改不了 —— 否则任何一台已入网设备都能改别人的名字。
        using var dir = new TempDir();
        var registry = new DeviceRegistry(System.IO.Path.Combine(dir.Path, "devices.jsonl"));

        await EnrollAsync(registry, "phone-1");

        var result = await registry.RequestRenameAsync("phone-1", "我叫别的", "不是它的凭据");

        Assert.Equal(EnrollStatus.BadToken, result.Status);
        Assert.Empty(await registry.PendingRenamesAsync());
    }

    [Fact]
    public async Task 请求之后是等待_批准才真的改_而且凭据不变()
    {
        using var dir = new TempDir();
        var registry = new DeviceRegistry(System.IO.Path.Combine(dir.Path, "devices.jsonl"));

        var credential = await EnrollAsync(registry, "phone-1");

        var requested = await registry.RequestRenameAsync("phone-1", "二号仓打包台", credential);
        Assert.Equal(EnrollStatus.Pending, requested.Status);

        // 还没批 —— 名字还没变。
        Assert.Equal("机位-phone-1", Assert.Single(await registry.DevicesAsync()).DeviceName);

        Assert.True(await registry.DecideRenameAsync("phone-1", approved: true));

        var device = Assert.Single(await registry.DevicesAsync());
        Assert.Equal("二号仓打包台", device.DeviceName);

        // ★ **凭据不变** —— 改名不是重新入网。换了凭据的话，那台手机下一次上传
        // 会撞 401，而它以为自己只是改了个名字（表现是「改完名字就传不上来了」）。
        Assert.NotNull(await registry.FindByCredentialAsync(credential));
    }

    [Fact]
    public async Task 拒绝之后名字不变()
    {
        using var dir = new TempDir();
        var registry = new DeviceRegistry(System.IO.Path.Combine(dir.Path, "devices.jsonl"));

        var credential = await EnrollAsync(registry, "phone-1");
        await registry.RequestRenameAsync("phone-1", "乱改的名字", credential);

        Assert.True(await registry.DecideRenameAsync("phone-1", approved: false));

        Assert.Equal("机位-phone-1", Assert.Single(await registry.DevicesAsync()).DeviceName);
    }

    [Fact]
    public async Task 手机反复请求时不重置已有的决定()
    {
        // ⚠️ 与入网那条同一个理由：手机是**轮询**的。重置成等待的话，
        // 用户刚点了同意，下一轮轮询又变回「还在等」—— 手机上永远转圈。
        using var dir = new TempDir();
        var registry = new DeviceRegistry(System.IO.Path.Combine(dir.Path, "devices.jsonl"));

        var credential = await EnrollAsync(registry, "phone-1");
        await registry.RequestRenameAsync("phone-1", "改过一次", credential);
        await registry.DecideRenameAsync("phone-1", approved: true);

        // 手机又轮询了一次。
        var again = await registry.RequestRenameAsync("phone-1", "改过一次", credential);

        Assert.Equal(EnrollStatus.Approved, again.Status);
    }

    [Fact]
    public async Task 批准落盘的名字也被量过尺子()
    {
        // 规格 §3.4.3 ②：「从**任何路径**写进来的名字都得是同一把尺子」——
        // 改名是**新开的一条路径**，所以这里也要量。
        using var dir = new TempDir();
        var registry = new DeviceRegistry(System.IO.Path.Combine(dir.Path, "devices.jsonl"));

        var credential = await EnrollAsync(registry, "phone-1");
        await registry.RequestRenameAsync("phone-1", "未命名机位abcdefghij", credential);
        await registry.DecideRenameAsync("phone-1", approved: true);

        var name = Assert.Single(await registry.DevicesAsync()).DeviceName;

        Assert.Equal("未命名机位ab", name);
        Assert.Equal(DeviceNameRules.MaxWidth, DeviceNameRules.Width(name));
    }
}
