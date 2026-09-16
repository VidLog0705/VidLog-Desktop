using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 规格 §3.1.1：存储将满时提前告警 + 主动安全收尾，而不是等崩溃。
/// </summary>
public class DiskSpaceGuardTests
{
    private const long Mb = 1024L * 1024;

    private sealed class FakeProbe : IDiskSpaceProbe
    {
        public long FreeBytes { get; set; }
        public Exception? Throw { get; set; }

        public long GetFreeBytes(string path) =>
            Throw is not null ? throw Throw : FreeBytes;
    }

    private static DiskSpaceGuard Build(FakeProbe probe) => new(probe, new DiskSpaceOptions
    {
        WarningFreeBytes = 2000 * Mb,
        StopFreeBytes = 500 * Mb,
        FinalizeHeadroomBytes = 500 * Mb,
    });

    [Fact]
    public void 余量充足时既不告警也不停止()
    {
        var verdict = Build(new FakeProbe { FreeBytes = 50_000 * Mb }).Check(@"C:\");

        Assert.False(verdict.ShouldWarn);
        Assert.False(verdict.ShouldFinalize);
        Assert.Null(verdict.Message);
    }

    [Fact]
    public void 低于警告线时告警但不停止()
    {
        // 还有 1.5 GB，够录一阵子 —— 提前告诉用户，但别把人家的录制掐了。
        var verdict = Build(new FakeProbe { FreeBytes = 1500 * Mb }).Check(@"C:\");

        Assert.True(verdict.ShouldWarn);
        Assert.False(verdict.ShouldFinalize);
        Assert.NotNull(verdict.Message);
    }

    [Fact]
    public void 低于停止线加收尾余量时主动安全收尾()
    {
        // 只剩 800 MB，而收尾本身还要再占一份（MKV 与 MP4 同时存在）——
        // 这时候不收尾，就会等到写失败，那才是真正的半成品。
        var verdict = Build(new FakeProbe { FreeBytes = 800 * Mb }).Check(@"C:\");

        Assert.True(verdict.ShouldWarn);
        Assert.True(verdict.ShouldFinalize);
        Assert.NotNull(verdict.Message);
    }

    [Fact]
    public void 刚好卡在收尾线之上时应收尾()
    {
        // 边界：停止线 500 MB + 余量 500 MB = 1000 MB。999 MB 必须收尾。
        var verdict = Build(new FakeProbe { FreeBytes = 999 * Mb }).Check(@"C:\");

        Assert.True(verdict.ShouldFinalize);
    }

    [Fact]
    public void 刚好等于收尾线时不必收尾()
    {
        var verdict = Build(new FakeProbe { FreeBytes = 1000 * Mb }).Check(@"C:\");

        Assert.False(verdict.ShouldFinalize);
        Assert.True(verdict.ShouldWarn);
    }

    [Fact]
    public void 探测不了磁盘时不得停止录制()
    {
        // I4 的同一条精神：远端/环境异常不得导致录制异常停止。
        // 误停一个正常工作的录制，比漏报一次磁盘告警更糟。
        var probe = new FakeProbe { Throw = new IOException("卷不存在") };

        var verdict = Build(probe).Check(@"Z:\");

        Assert.False(verdict.ShouldFinalize);
        Assert.False(verdict.ShouldWarn);
        Assert.NotNull(verdict.Message);
    }

    [Fact]
    public void 默认阈值必须非零_这是配置坏掉时的硬兜底()
    {
        // 规格 §3.1.1：阈值由配置下发，**且必须有本地硬兜底值**。
        // 兜底值是 0 就等于没有兜底 —— 磁盘写满了也不会收尾。
        var options = new DiskSpaceOptions();

        Assert.True(options.WarningFreeBytes > 0);
        Assert.True(options.StopFreeBytes > 0);
        Assert.True(options.FinalizeHeadroomBytes > 0);
        Assert.True(options.WarningFreeBytes > options.StopFreeBytes, "警告线必须在停止线之上");
    }

    [Fact]
    public void 真实探测能读出当前盘的余量()
    {
        var guard = new DiskSpaceGuard(new DriveSpaceProbe());

        var verdict = guard.Check(System.IO.Path.GetTempPath());

        Assert.True(verdict.FreeBytes > 0, "临时目录所在的盘应当有正数余量");
    }
}
