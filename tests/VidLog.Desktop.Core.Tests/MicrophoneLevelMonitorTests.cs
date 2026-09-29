using System.Diagnostics;
using VidLog.Desktop.Core.Camera;
using VidLog.Desktop.Core.Media;
using VidLog.Desktop.Core.Recording;

namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 麦克风电平（配置向导第 5 步那条音量条）。
/// </summary>
/// <remarks>
/// ⚠️ <b>本机没有麦克风</b>（2026-09-29 实测 `Could not enumerate audio only devices`）
/// ⇒「真麦克风上跳不跳」这一档**验不了**。
/// 验得了的是：① 那些**参数**拼得对不对；② 那些**行**解析得对不对；
/// ③ 用 `sine` 合成源喂它时**电平真的会动**（那条最要紧 —— 它抓得住
/// 「接线接上了但值永远是 0」那种错）。
/// </remarks>
public class MicrophoneLevelMonitorTests
{
    // ─────────────────────────────────────────────
    // 行的解析（不依赖任何设备）
    // ─────────────────────────────────────────────

    [Fact]
    public void 满刻度和静音各对应到两端()
    {
        Assert.Equal(1.0, MicrophoneLevelMonitor.ParseLevel("lavfi.astats.Overall.RMS_level=0"), 3);
        Assert.Equal(0.0, MicrophoneLevelMonitor.ParseLevel($"lavfi.astats.Overall.RMS_level={MicrophoneLevelMonitor.FloorDb}"), 3);
    }

    [Fact]
    public void 静音时报的是负无穷_要当成静而不是抛()
    {
        // ⚠️ 实测形状：没人说话时 astats 报的是 `-inf`。
        // 直接 double.Parse 会抛（或者在某些区域设置下变成 NaN）——
        // 而「没人说话时是静的」**本来就是正常状态**，不能当异常。
        Assert.Equal(0.0, MicrophoneLevelMonitor.ParseLevel("lavfi.astats.Overall.RMS_level=-inf"), 3);
        Assert.Equal(0.0, MicrophoneLevelMonitor.ParseLevel("lavfi.astats.Overall.RMS_level=nan"), 3);
        Assert.Equal(0.0, MicrophoneLevelMonitor.ParseLevel("lavfi.astats.Overall.RMS_level="), 3);
    }

    [Fact]
    public void 认不出来的行是静_而不是崩溃()
    {
        // ffmpeg 会往这条流上打别的东西（版本、别的 filter 的输出）。
        // 认不出来就当 0 —— 一帧读不出来绝不能让那一步崩。
        Assert.Equal(0.0, MicrophoneLevelMonitor.ParseLevel("随便一行什么都不是"), 3);
        Assert.Equal(0.0, MicrophoneLevelMonitor.ParseLevel(string.Empty), 3);
    }

    [Fact]
    public void 中间的电平是线性的而且夹在零到一之间()
    {
        // -60dB ⇒ 0、-30dB ⇒ 0.5、0dB ⇒ 1（线性映射）。
        Assert.Equal(0.5, MicrophoneLevelMonitor.ParseLevel("lavfi.astats.Overall.RMS_level=-30"), 3);

        // 比静音门槛还低 ⇒ 0（不出现负数）；正的 dB 也只到 1。
        Assert.Equal(0.0, MicrophoneLevelMonitor.ParseLevel("lavfi.astats.Overall.RMS_level=-90"), 3);
        Assert.Equal(1.0, MicrophoneLevelMonitor.ParseLevel("lavfi.astats.Overall.RMS_level=6"), 3);
    }

    // ─────────────────────────────────────────────
    // 命令的拼装
    // ─────────────────────────────────────────────

    [Fact]
    public void 命令只开音频_而且与录制共用同一处拼装()
    {
        var args = MicrophoneLevelMonitor.BuildArguments("话筒").ToList();

        // ⚠️ 与录制那一档**用同一个函数**（`FfmpegCameraCapture.AudioInputArguments`）——
        // 抄两份的话迟早有一份漏掉 `-rtbufsize`，而漏掉的表现是「偶尔丢样本」。
        //
        // ⚠️ 判据**不手算位置**：我前两版分别写 `Take(5)` 与「从 audio= 往回数 4 个」，
        // 两次都数错了（前者漏了全局选项、后者漏了 `-f`）。
        // 改成「那 5 项作为**连续子序列**出现」—— 与前后各有什么无关。
        var audio = FfmpegCameraCapture.AudioInputArguments("话筒", "64M").ToList();

        Assert.True(
            Enumerable.Range(0, Math.Max(0, args.Count - audio.Count + 1))
                .Any(i => args.Skip(i).Take(audio.Count).SequenceEqual(audio)),
            $"音频参数应当原样出现在命令里：{string.Join(' ', args)}");

        // 不该去碰相机（dshow 独占：这一步能抢到相机的话，录制就抢不到了）。
        Assert.DoesNotContain("video=", args);
    }

    [Fact]
    public void reset等于一是承重的_否则音量条从头到尾一动不动()
    {
        // ⚠️ 不给 `reset=1` 的话 astats 只在整个流**结束时**汇总一次，
        // 于是音量条永远不动 —— 而那看起来像「麦克风坏了」。
        var args = MicrophoneLevelMonitor.BuildArguments("话筒").ToList();
        var filters = args[args.IndexOf("-af") + 1];

        Assert.Contains("reset=1", filters, StringComparison.Ordinal);

        // 电平要走 stdout（`ametadata=print` 的 `file=-`）：
        // 走 stderr 的话它会与「起不来时的原因」混在一条路上。
        Assert.Contains("file=-", filters, StringComparison.Ordinal);
    }

    [Fact]
    public void 空麦克风名字直接抛()
    {
        Assert.Throws<ArgumentException>(() => MicrophoneLevelMonitor.BuildArguments("  "));
    }

    // ─────────────────────────────────────────────
    // ★ 用合成源验「电平真的会动」
    // ─────────────────────────────────────────────

    /// <summary>
    /// 喂一个真的会响的合成源，看电平会不会动。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>这条是这一组里最要紧的</b>：它抓的是「接线接上了、但值永远是 0」
    /// 那一类错 —— 界面上表现为「音量条一动不动」，而那与「麦克风没声音」
    /// **长得一模一样**，用户没法分辨。
    /// <para>
    /// ⚠️ 它**不走 <c>StartAsync</c>**（那个的输入是 dshow 麦克风，本机没有），
    /// 而是直接跑同一串**滤镜参数**、从 stdout 读同一形状的行 ——
    /// 验的是「读端能不能把那些行变成会动的数」。
    /// </para>
    /// </remarks>
    [RequiresFfmpegFact]
    public async Task 合成音源真的会让电平动起来()
    {
        var ffmpeg = FfmpegLocator.TryFind()!;

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=3",
            "-af", "astats=metadata=1:reset=1,"
                + "ametadata=print:key=lavfi.astats.Overall.RMS_level:file=-",
            "-f", "null", "-",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        _ = Task.Run(async () => { try { await process.StandardError.ReadToEndAsync(); } catch { } });

        var levels = new List<double>();
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < TimeSpan.FromSeconds(10) && levels.Count < 3)
        {
            var line = await process.StandardOutput.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            if (line.Contains("RMS_level=", StringComparison.Ordinal))
            {
                levels.Add(MicrophoneLevelMonitor.ParseLevel(line));
            }
        }

        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
        }

        Assert.True(levels.Count >= 3, $"应当读到好几行电平，实际 {levels.Count} 行");

        // ⚠️ 一个 440Hz 的正弦**每一帧都是响的** ⇒ 电平必须明显大于 0。
        // 「全是 0」正是要防的那种接线错。
        Assert.True(
            levels.Any(l => l > 0.3),
            $"正弦源的电平应当明显大于 0，实际读到：{string.Join(", ", levels.Select(l => l.ToString("0.###")))}");
    }
}
