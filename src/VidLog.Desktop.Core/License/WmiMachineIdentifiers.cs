using System.Management;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.Core.License;

/// <summary>
/// 从 WMI 读三个硬件标识（`docs/04-许可设计.md` §2.1）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>只读这三个</b>：主板序列号（<c>Win32_BaseBoard</c>）、
/// CPU ID（<c>Win32_Processor</c>）、BIOS 版本（<c>Win32_BIOS</c>）。
/// </para>
/// <list type="bullet">
/// <item><b>硬盘序列号不要</b> —— 换硬盘很常见，放进去等于自找售后。</item>
/// <item><b>网卡 MAC 不要</b> —— 插拔网卡、虚拟网卡、USB 网卡都会变。</item>
/// <item><b>系统 MachineGuid 不要</b> —— 重装系统就变。</item>
/// </list>
/// <para>
/// ⚠️ <b>读不到不是错误</b>：某个查询失败（WMI 服务被关、权限不够、非 Windows）
/// 时返回空串，由 <see cref="MachineCode"/> 把那一段当成全零段。
/// 那是设计里的降级路径（§2.4），**但界面必须把「降级」显示出来** ——
/// 全零段的机器码没有任何区分度，同型号的机器会互相匹配。
/// </para>
/// </remarks>
public sealed class WmiMachineIdentifiers : IMachineIdentifiers
{
    private readonly IAppLogger _logger;

    public WmiMachineIdentifiers(IAppLogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public string MotherboardSerial => Query("Win32_BaseBoard", "SerialNumber");

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public string ProcessorId => Query("Win32_Processor", "ProcessorId");

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public string BiosVersion => Query("Win32_BIOS", "SMBIOSBIOSVersion");

    /// <summary>一次 WMI 查询；任何失败都返回空串并记一条。</summary>
    /// <remarks>
    /// 超时与异常都要吞掉：许可读不出来只是「录不了」，而它**不该让程序起不来**
    /// （与 I4「坏配置不得影响录制」同一条精神 —— 这里更进一层：坏配置也不该影响**启动**）。
    /// <para>
    /// ⚠️ <b>WMI 只有 Windows 有</b>，所以这里显式判一下平台 ——
    /// 不判的话分析器会报 CA1416（而本仓的标准是 0 警告），
    /// 更要紧的是：将来真在别的平台上跑，那会是一个平台异常而不是「读不到」。
    /// </para>
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private string Query(string wmiClass, string property)
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT {property} FROM {wmiClass}");

            foreach (var item in searcher.Get())
            {
                using var record = (ManagementObject)item;
                var value = record[property]?.ToString();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            _logger.Log(LogLevel.Warn, "许可", $"{wmiClass}.{property} 读不到");
            return string.Empty;
        }
        catch (Exception ex)
        {
            // WMI 被禁用、权限不够、非 Windows 平台 —— 都走这条。
            _logger.Log(LogLevel.Warn, "许可", $"{wmiClass}.{property} 查询失败：{ex.Message}");
            return string.Empty;
        }
    }
}
