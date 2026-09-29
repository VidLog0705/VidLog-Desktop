namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 开**真 DirectShow 设备**（摄像头 / 麦克风）的那些测试**串行跑**。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有这个集合</b>：DirectShow 相机是**独占**的（§25 实测）——
/// 两个测试**并行**去开同一台相机时，后一个会拿到
/// <c>Could not set video options</c> 或退出码 <c>-5</c>（EIO），
/// 而代码一行没错。
/// </para>
/// <para>
/// ⚠️ 2026-09-30 实测踩到：新加的「真相机预览」那条**单独跑 6 次全过**，
/// 混在全量里跑就红一次（紧接着预览的那次录制拿到 <c>-5</c>）——
/// 因为 xUnit 默认**按类并行**，而预览与采集是两个类。
/// </para>
/// <para>
/// ⚠️ <b>这不是「把 flake 藏起来」，而是把那个竞态从根上拿掉</b>：
/// 设备冲突只在**同时**有两个进程开它时才有。串行之后，同一个时刻只有一个。
/// 与 <see cref="HttpListenerCollection"/> 是同一条理由、同一个手法。
/// </para>
/// <para>
/// ⚠️ 代价是这一族测试不再并行 —— 它们本来就要真开设备、真录一段，
/// 是全套里最慢的几个。**这个代价值得**：一条随机的红会让人开始无视红。
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class DshowDeviceCollection
{
    public const string Name = "dshow-device";
}
