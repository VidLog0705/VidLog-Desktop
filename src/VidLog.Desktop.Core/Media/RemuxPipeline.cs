using System.Text;

namespace VidLog.Desktop.Core.Media;

/// <summary>remux 的结果。</summary>
/// <param name="Succeeded">是否成功产出可用的 MP4。</param>
/// <param name="FailureReason">失败原因；成功时为 <see langword="null"/>。</param>
public sealed record RemuxResult(bool Succeeded, string? FailureReason);

/// <summary>
/// MKV 中间容器 → 无损 remux MP4。
/// </summary>
/// <remarks>
/// 规格 §3.1.4：产出必须是**广兼容、可独立播放**的成品。
/// <para>
/// <b>为什么录制期用 MKV 而不是直接写 MP4</b>：MP4 的索引（moov）在文件末尾，
/// 进程被杀时那段没写下去，整个文件就是彻底打不开的废件 —— 正好撞上规格 §8
/// 的「录制中掉电/进程被杀」和 I9 的「不产生无法播放的半成品」。
/// MKV 是分段结构，写到哪算哪，剩余的也能解出来。
/// 所以：**录制期 MKV，停下来之后 remux 成 MP4**。
/// </para>
/// <para>
/// remux 用 <c>-c copy</c>，不重编码 —— 画面零损失，也快（不涉及 GPU/CPU 编码）。
/// </para>
/// </remarks>
public sealed class RemuxPipeline
{
    private readonly string _ffmpegPath;
    private readonly IProcessRunner _runner;

    public RemuxPipeline(string ffmpegPath, IProcessRunner runner)
    {
        _ffmpegPath = ffmpegPath;
        _runner = runner;
    }

    /// <summary>
    /// 把 <paramref name="sourcePath"/> 无损转成 <paramref name="destinationPath"/>。
    /// </summary>
    /// <remarks>
    /// **失败时不得删除源文件。** MKV 是此刻唯一存在的副本 ——
    /// 删掉它就违反了 I2（任何录像出厂后任何时刻至少存在一份完整副本）。
    /// 本方法不负责清理源文件，那是上层的事。
    /// </remarks>
    public async Task<RemuxResult> RemuxAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            return new RemuxResult(false, $"源文件不存在：{sourcePath}");
        }

        var arguments = new[]
        {
            "-hide_banner",
            "-v", "error",
            "-i", sourcePath,
            "-c", "copy",              // 不重编码
            "-movflags", "+faststart", // moov 挪到文件头，便于边下边播
            "-y", destinationPath,
        };

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_ffmpegPath, arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            return new RemuxResult(false, $"调用 FFmpeg 失败：{ex.Message}");
        }

        if (!result.Succeeded)
        {
            return new RemuxResult(
                false,
                $"FFmpeg 退出码 {result.ExitCode}：{Truncate(result.StandardError)}");
        }

        if (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length == 0)
        {
            return new RemuxResult(false, "remux 退出码为 0，但没有产出可用文件");
        }

        return new RemuxResult(true, null);
    }

    /// <summary>
    /// 把一场录制的多个分段**合并成同一个成品**，仍然是 <c>-c copy</c> 无损。
    /// </summary>
    /// <remarks>
    /// 需求方 2026-10-12：一个单号就是一个文件 —— 之前的「预录开场段 + 各滚段」
    /// 是 <c>_000</c>、<c>_001</c>… 好几个文件，现在收尾时合成一个。
    /// <para>
    /// 走 ffmpeg 的 concat demuxer：<c>-f concat -safe 0 -i &lt;列表&gt; -c copy</c>，
    /// 它给每一段自动加上前一段的时长作为时间戳偏移，所以各段自带的时间戳
    /// （每段都从 0 起）接起来是单调的。
    /// </para>
    /// <para>
    /// ⚠️ <b>只传一段时直接走 <see cref="RemuxAsync"/></b>，命令一模一样 ——
    /// 于是「一段」与「多段」在调用方那边**没有分支**，少一处可能走岔的地方。
    /// </para>
    /// <para>
    /// ⚠️ <b>失败时不得删除源文件</b>，与 <see cref="RemuxAsync"/> 同一条（I2）：
    /// 合并到一半挂了，那几段 MKV 仍然是此刻唯一的副本。
    /// </para>
    /// <para>
    /// ⚠️ <b>已知未验</b>：预录段与正片是**两个独立 ffmpeg 进程**写的，各自带
    /// AAC 的 encoder delay（priming，约 21–43 ms）⇒ 接缝处可能有极短的一处不连续。
    /// 真机录一段带人声的听一下就知道；要彻底消掉只能重编码。
    /// </para>
    /// </remarks>
    /// <param name="sourcePaths">按段号排好序的源文件；**至少一个**。</param>
    public async Task<RemuxResult> MergeAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (sourcePaths.Count == 0)
        {
            return new RemuxResult(false, "没有任何分段可以合并");
        }

        if (sourcePaths.Count == 1)
        {
            return await RemuxAsync(sourcePaths[0], destinationPath, cancellationToken);
        }

        foreach (var path in sourcePaths)
        {
            if (!File.Exists(path))
            {
                // 与「一段不存在」同一条口径：说清是哪一段，不合并半个东西出来。
                return new RemuxResult(false, $"源分段不存在：{path}");
            }
        }

        var listPath = Path.Combine(Path.GetTempPath(), $"vidlog-concat-{Guid.NewGuid():N}.txt");

        try
        {
            // ⚠️ 显式 UTF-8（无 BOM）：路径里可能带中文（本机用户名那一层就是），
            // 让 ffmpeg 按系统代码页猜的话它会认不出文件 —— 而报出来的错是
            // 「No such file or directory」，看起来像文件没了。
            await File.WriteAllLinesAsync(
                listPath,
                sourcePaths.Select(ConcatLine),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            var arguments = new[]
            {
                "-hide_banner",
                "-v", "error",
                "-f", "concat",
                "-safe", "0",              // 列表里是绝对路径，默认的安全模式会拒
                "-i", listPath,
                "-c", "copy",              // 不重编码
                "-movflags", "+faststart",
                "-y", destinationPath,
            };

            ProcessResult result;
            try
            {
                result = await _runner.RunAsync(_ffmpegPath, arguments, cancellationToken);
            }
            catch (Exception ex)
            {
                return new RemuxResult(false, $"调用 FFmpeg 失败：{ex.Message}");
            }

            if (!result.Succeeded)
            {
                return new RemuxResult(
                    false,
                    $"FFmpeg 退出码 {result.ExitCode}：{Truncate(result.StandardError)}");
            }

            if (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length == 0)
            {
                return new RemuxResult(false, "合并退出码为 0，但没有产出可用文件");
            }

            return new RemuxResult(true, null);
        }
        finally
        {
            // 收尾要**干净**：这份列表留在 %TEMP% 里没有任何人会用，
            // 而它每次合并都新增一个。删不掉不算事故（下次开机系统会清），
            // 所以不抛 —— 真抛出去会把一次成功的合并在最后一步报成失败。
            try
            {
                File.Delete(listPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>concat 列表里的一行。<b>路径在单引号里，反斜杠要正过来。</b></summary>
    private static string ConcatLine(string path)
    {
        // 单引号是列表格式的分隔符，路径里真出现它要按 ffmpeg 的规矩转义
        // （先闭引号、再一个转义单引号、再开引号）。
        var safe = path.Replace('\\', '/').Replace("'", "'\\''");

        return $"file '{safe}'";
    }

    private static string Truncate(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
