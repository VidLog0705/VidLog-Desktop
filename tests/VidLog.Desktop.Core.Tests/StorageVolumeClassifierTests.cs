using VidLog.Desktop.Core.Configuration;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 「这条路径落在什么样的存储上」（2026-10-13 需求方裁定：加备份位置时判卷）。
/// </summary>
/// <remarks>
/// <para>
/// 它挡的是**一类真会丢证据的配置**：把本机的目录当成「别处那一份」。
/// 一旦收下，清理入口会开，而两份其实躺在同一块盘上 —— 删完只剩零份。
/// 所以在加进去那一刻就拒掉，比事后在清理那里兜便宜得多。
/// </para>
/// <para>
/// ⚠️ 本套件与 CI 都只在 Windows 上跑（产品只有 Windows 端），所以这里**不设平台分支**：
/// 非 Windows 上 <c>Classify</c> 一律回 <see cref="StorageVolumeKind.Unknown"/>，
/// 那几条会红 —— 那是要人看见的，不是要静默跳过的。
/// </para>
/// <para>
/// ⚠️ <b>没验到的那一档</b>：<see cref="StorageVolumeKind.VirtualDisk"/>（网盘挂载出来的盘）
/// 要一台装了 Dokan / WinFsp / CloudDrive2 的机器才造得出来，本机没有。
/// 所以下面验的是**另外三种**加上「裁定到话」这一层纯函数。
/// </para>
/// </remarks>
public class StorageVolumeClassifierTests
{
    // ─────────────────────────────────────────────
    // 判卷（纯函数）
    // ─────────────────────────────────────────────

    [Fact]
    public void 四种卷各自裁成什么()
    {
        // ⚠️ 只有网络位置**直接收**。另外三种都要用户过一道（或直接拒）——
        // 「判不出来」不能当「行」放过去：那正好是上面那条丢证据的路。
        Assert.Equal(BackupPathDecision.Accept, StorageVolumeClassifier.DecideForBackup(StorageVolumeKind.Network));

        Assert.Equal(
            BackupPathDecision.RejectLocalPhysical,
            StorageVolumeClassifier.DecideForBackup(StorageVolumeKind.LocalPhysical));

        Assert.Equal(
            BackupPathDecision.ConfirmVirtualDisk,
            StorageVolumeClassifier.DecideForBackup(StorageVolumeKind.VirtualDisk));

        Assert.Equal(
            BackupPathDecision.ConfirmUnknown,
            StorageVolumeClassifier.DecideForBackup(StorageVolumeKind.Unknown));
    }

    [Fact]
    public void 拒收那句话要说清为什么_而且不含标记符号()
    {
        var words = StorageVolumeClassifier.Explain(BackupPathDecision.RejectLocalPhysical, @"D:\录像");

        // 要把「哪条路径」与「为什么不行」都说出来 —— 只回一句「不能用」，
        // 用户会以为程序坏了。
        Assert.Contains(@"D:\录像", words);
        Assert.Contains("本机的一块物理盘", words);
        Assert.Contains("别处", words);

        // ⚠️ 它原样落进设置页那行 TextBlock / MessageBox，两处都不认 Markdown。
        Assert.DoesNotContain("**", words);
    }

    [Fact]
    public void 放行那句话不带路径_因为对话框里本来就有()
    {
        // 另外三种都是「这个位置不行 / 你确认一下」那种要说清是哪儿的，
        // 只有「收下」这一支不需要 —— 别为它编一句多余的话。
        Assert.Equal("这是网络位置，可以用。", StorageVolumeClassifier.Explain(BackupPathDecision.Accept, @"\\nas\v"));
    }

    [Fact]
    public void 另外两种确认的话也都说清了后果且不含标记符号()
    {
        foreach (var decision in new[] { BackupPathDecision.ConfirmVirtualDisk, BackupPathDecision.ConfirmUnknown })
        {
            var words = StorageVolumeClassifier.Explain(decision, @"Z:\vidlog");

            Assert.Contains(@"Z:\vidlog", words);
            Assert.DoesNotContain("**", words);
        }
    }

    // ─────────────────────────────────────────────
    // 判卷之前先判是什么卷
    // ─────────────────────────────────────────────

    [Fact]
    public void 空路径与空白判不出来()
    {
        // 设置页那个输入框一开始就是空的；空要落到「你确认一下」，
        // **不能**落到「这是本机盘」——那会把用户还没填的位置先骂一遍。
        foreach (var empty in new[] { null, "", "   " })
        {
            Assert.Equal(StorageVolumeKind.Unknown, StorageVolumeClassifier.Classify(empty));
        }
    }

    [Fact]
    public void UNC_一望即知是网络位置()
    {
        // `\\服务器\共享` 没有盘符，任何盘符探查都够不着它 —— 这一支要在最前面挡下。
        Assert.Equal(
            StorageVolumeKind.Network, StorageVolumeClassifier.Classify(@"\\nas\vidlog"));
        Assert.Equal(
            StorageVolumeKind.Network, StorageVolumeClassifier.Classify(@"\\192.168.101.9\vidlog\2026"));

        // 前后带空格、带引号（从资源管理器地址栏复制过来的那种）也要认。
        Assert.Equal(
            StorageVolumeKind.Network, StorageVolumeClassifier.Classify("  \"\\\\nas\\vidlog\"  "));
    }

    [Fact]
    public void 本机系统盘那块盘_判成本机物理盘()
    {
        // ⚠️ 这条是**整套里唯一真去问系统**的：前面 UNC 那支在最前面就返回了，
        // 一行 Win32 都没调。这条走完 DriveType → WNet → QueryDosDevice → 设备名前缀
        // 一整条链，链上任何一环断了它都会红。
        var windows = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        Assert.NotNull(windows);

        var kind = StorageVolumeClassifier.Classify(windows);

        Assert.True(
            kind == StorageVolumeKind.LocalPhysical,
            $"`{windows}` 是本机的系统盘，应当判成本机物理盘，实际判成 {kind}");

        // 判成什么就裁成什么 —— 这两步之间不许有第三个答案。
        Assert.Equal(BackupPathDecision.RejectLocalPhysical, StorageVolumeClassifier.DecideForBackup(kind));
    }

    [Fact]
    public void 盘符没插着的时候判不出来_而且不算拒()
    {
        // ⚠️ 这条的输入是**现挑一个没被占用的盘符**，不是写死 `Q:` ——
        // 写死的话，碰上一台真把 Q: 用起来的机器，这条测的就不是「盘不在」了。
        var used = DriveInfo.GetDrives()
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();

        var letter = "QRSTUVWXYZ".FirstOrDefault(candidate => !used.Contains(candidate));
        Assert.True(letter != '\0', "这台机器 Q–Z 全被占用了 —— 这条用例要换一组字母");

        var kind = StorageVolumeClassifier.Classify($@"{letter}:\没有这块盘");

        // 「盘不在」是**判不出来**，不是「这是本机盘」—— 后者会把一块真能用的
        // NAS（换台机器挂到别的盘符）当场拒掉，而用户不知道自己做错了什么。
        Assert.Equal(StorageVolumeKind.Unknown, kind);
        Assert.Equal(BackupPathDecision.ConfirmUnknown, StorageVolumeClassifier.DecideForBackup(kind));
    }

    [Fact]
    public void 什么怪输入都不许抛()
    {
        // 判定链上有三次会落空的系统调用（DriveInfo / WNetGetConnection /
        // QueryDosDevice），而输入是用户**手敲**的 —— 敲到一半什么样都有。
        // 抛出来的表现是「设置页里打字打到一半，程序没了」。
        var odds = new[] { null, "", "   ", "vidlog", @"C:\<>:|?*", @"\\?\x", @"\\", "\"", @"""\\nas""x" };

        foreach (var odd in odds)
        {
            var kind = StorageVolumeClassifier.Classify(odd);

            // 连带把「判出来的结果能说成人话」也走一遍 —— 少一步就是又一条没人走过的路。
            _ = StorageVolumeClassifier.Explain(
                StorageVolumeClassifier.DecideForBackup(kind), odd ?? "");
        }
    }
}
