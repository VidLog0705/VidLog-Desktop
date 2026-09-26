using System.Text;
using System.Text.Json;
using VidLog.Desktop.Core.Scanning;

namespace VidLog.Desktop.Core.Recording;

/// <summary>
/// 一次「扫到别的面单」（规格 §3.3.2 错码保护触发）。
/// </summary>
/// <remarks>
/// 形状照母仓 <c>docs/02-数据模型.md</c> §1.7 那张表，四个字段**逐字对齐**
/// （两端要能读同一份记录）：
/// <c>SessionId</c> / <c>ExpectedWaybill</c> / <c>ScannedWaybill</c> / <c>OccurredAt</c>。
/// <para>
/// > **这是一条事后诊断记录，不是控制流的输入。**
/// </para>
/// <para>
/// 扫到错码时的行为是「不停录 + 语音提示」，判定**不依赖**本表 ——
/// 所以写失败**绝不能影响录制**。
/// </para>
/// </remarks>
public sealed record ScanErrorEvent(
    string SessionId,
    WaybillNumber ExpectedWaybill,
    WaybillNumber ScannedWaybill,
    DateTimeOffset OccurredAt)
{
    public string ToJson() => JsonSerializer.Serialize(new ScanErrorDto(
        SessionId,
        ExpectedWaybill.Value,
        ScannedWaybill.Value,
        OccurredAt.ToUniversalTime().ToString("O")));

    public static ScanErrorEvent FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<ScanErrorDto>(json)
            ?? throw new InvalidOperationException("错误扫描记录解析成了空");

        return new ScanErrorEvent(
            dto.SessionId,
            WaybillNumber.Parse(dto.ExpectedWaybill),
            WaybillNumber.Parse(dto.ScannedWaybill),
            DateTimeOffset.Parse(dto.OccurredAt).ToLocalTime());
    }

    private sealed record ScanErrorDto(
        string SessionId, string ExpectedWaybill, string ScannedWaybill, string OccurredAt);
}

/// <summary>
/// 按行追加的错误扫描记录。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>JsonLinesPunchLog</c> / <c>JsonLinesLabelStore</c> 同样的形态与理由
/// （见 <c>RecordingIndex</c>）：追加一行比改写整个文件便宜，
/// 且掉电时已写的行仍然完整。
/// </para>
/// <para>
/// ⚠️ 它补的是一条**规格欠账**：<c>docs/01-行为规格书.md</c> §6.1
/// 「必须保存的事实」里点名要有「错误扫描（错码保护触发的事件，诊断用）」，
/// 而 2026-09-26 之前**两端都没有实现** —— 错码保护只做了界面提示与播报。
/// </para>
/// </remarks>
public sealed class ScanErrorLog
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ScanErrorLog(string path) => _path = path;

    /// <summary>记一条，返回之后它已经在盘上了。</summary>
    public async Task AppendAsync(ScanErrorEvent scanError, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(
                _path,
                scanError.ToJson() + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ScanErrorEvent>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var result = new List<ScanErrorEvent>();

        foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                result.Add(ScanErrorEvent.FromJson(line));
            }
            catch (JsonException)
            {
                // 坏行跳过 —— 与索引、打点、标签同一条规矩。
            }
        }

        return result;
    }
}
