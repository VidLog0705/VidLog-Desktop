using VidLog.Desktop.Core.Upload;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 机位名的宽度规矩（规格 §3.4.3 ② / §3.4.5 ③）。
/// </summary>
/// <remarks>
/// <para>
/// 需求方 2026-09-23：「机位名只允许 12 个字符内，一个汉字两个字符，
/// 一个字母 1 个字符，可以 12 个字母或者 6 个汉字」。
/// </para>
/// <para>
/// ⚠️ 规格 §3.4.3 ② 点名的是**这个字段**：「上限属于『机位名』**这个字段**，
/// 不只属于那个输入框……从**任何路径**写进来的名字都得是同一把尺子」，
/// 且 §3.4.5 ③「**两端必须用同一套换算**」。
/// 手机端那一半在 `device_identity.dart`。
/// </para>
/// </remarks>
public class DeviceNameRulesTests
{
    // ─────────────────────────────────────────────
    // ★ 跨仓固定向量
    // ─────────────────────────────────────────────

    /// <summary>
    /// 这一组样例**两端各写一份**，值必须逐字相同。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这是**跨仓的固定向量**（与许可那条 `签发工具产出的码_客户端验得过`
    /// 同一个手法）：两个仓没法共享代码，而「两端同一把尺子」是规格点名的要求。
    /// 只在一端改换算 ⇒ 那一端会红，而另一端的同名测试**仍然绿** ——
    /// 所以这条测试的注释里写明「另一端在 `device_identity_test.dart`」。
    /// </remarks>
    public static TheoryData<string, int, string> Vectors => new()
    {
        // 输入、宽度、Clamp 之后
        { "打包手机-1", 10, "打包手机-1" },          // 4 汉字(8) + '-' + '1'
        { "abcdefghijkl", 12, "abcdefghijkl" },      // 12 个字母正好
        { "abcdefghijklm", 13, "abcdefghijkl" },     // 第 13 个被丢掉
        { "未命名机位", 10, "未命名机位" },            // 5 个汉字 = 10 格
        { "未命名机位abc", 13, "未命名机位ab" },       // 10 + a + b = 12，c 超了
        { "🎬录像", 6, "🎬录像" },                   // emoji 算 2
        { "", 0, "" },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void 宽度与截断_跟跨仓向量一致(string input, int expectedWidth, string expectedClamped)
    {
        Assert.Equal(expectedWidth, DeviceNameRules.Width(input));
        Assert.Equal(expectedClamped, DeviceNameRules.Clamp(input));
    }

    // ─────────────────────────────────────────────
    // 那两个坑
    // ─────────────────────────────────────────────

    [Fact]
    public void 六格汉字是十二格_而不是六个字符()
    {
        // ⚠️ 这一条钉的是**最要紧的那个坑**：`string.Length` 数的是 UTF-16
        // code unit —— 「未命名机位」的 `Length` 是 **5**，而它占 **10 格**。
        // 用 `Length` 判上限的话，6 个汉字（12 格，正好到顶）会被判成 6 ⇒
        // 全部放行，**上限放宽了一倍**。
        Assert.Equal(5, "未命名机位".Length);
        Assert.Equal(10, DeviceNameRules.Width("未命名机位"));
    }

    [Fact]
    public void 截断不劈字_emoji也算两格()
    {
        // ⚠️ 按 `char` 砍在代理对中间会留下一个孤零零的半字符 ——
        // 那在界面上是个「豆腐块」，而且它还是个**非法的 UTF-8 序列**，
        // 传给电脑端会变成乱码。按 rune 走就不会。
        var clamped = DeviceNameRules.Clamp("🎬🎬🎬🎬🎬🎬🎬");   // 7 个 emoji = 14 格

        Assert.Equal("🎬🎬🎬🎬🎬🎬", clamped);              // 6 个 = 12 格
        Assert.Equal(12, DeviceNameRules.Width(clamped));

        // 没有半个代理对残留（`Length` 是偶数、且每个都是成对的）。
        Assert.Equal(12, clamped.Length);
    }

    [Fact]
    public void 空与_null()
    {
        Assert.Equal(0, DeviceNameRules.Width(null));
        Assert.Equal(0, DeviceNameRules.Width(string.Empty));
        Assert.Equal(string.Empty, DeviceNameRules.Clamp(null));
        Assert.Equal(string.Empty, DeviceNameRules.Clamp(string.Empty));
    }

    // ─────────────────────────────────────────────
    // 入网那条路上的三处量尺
    // ─────────────────────────────────────────────

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vidlog-name-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>22 格：10 个汉字 + 12 个字母 —— 远超上限。</summary>
    private const string LongName = "未命名机位abcdefghij";

    [Fact]
    public async Task 超长的机位名在入网时就被截掉_待批准与落盘都一样()
    {
        // ⚠️ 规格 §3.4.3 ② 点名的是**这个字段**（「从**任何路径**写进来的名字
        // 都得是同一把尺子」）。不截的话，电脑端的待批准弹窗上显示一个超长名字、
        // 而批准之后落盘的是截过的 —— **用户看到的两处不一样**。
        using var dir = new TempDir();
        var registry = new DeviceRegistry(System.IO.Path.Combine(dir.Path, "devices.jsonl"));

        var session = await registry.OpenSessionAsync();
        await registry.RequestAsync("phone-1", LongName, session.Token);

        // ① 待批准列表里就是截过的。
        var shown = Assert.Single(await registry.PendingAsync()).DeviceName;
        Assert.Equal("未命名机位ab", shown);
        Assert.Equal(DeviceNameRules.MaxWidth, DeviceNameRules.Width(shown));

        // ② 落盘那份也是截过的。
        await registry.DecideAsync("phone-1", approved: true);
        await registry.ClaimAsync("phone-1", session.Token);

        Assert.Equal("未命名机位ab", Assert.Single(await registry.DevicesAsync()).DeviceName);
    }

    [Fact]
    public async Task 登记簿里手改出来的超长名字_读出来也被截掉()
    {
        // ⚠️ 这一条挡的是**这次改动之前就写进去的**那些：手改过的登记簿、
        // 旧版本存下的长名字。只量写入端的话，那些老数据会一路显示到界面上。
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "devices.jsonl");

        await File.WriteAllTextAsync(path,
            $$"""
            {"DeviceId":"phone-9","DeviceName":"{{LongName}}","Credential":"x","ApprovedAt":"2026-09-27T00:00:00+00:00"}

            """);

        var registry = new DeviceRegistry(path);

        Assert.Equal("未命名机位ab", Assert.Single(await registry.DevicesAsync()).DeviceName);
    }
}
