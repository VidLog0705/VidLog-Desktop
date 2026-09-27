namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 起真 <c>HttpListener</c> 的那些测试**串行跑**。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有这个集合</b>：那些测试用「先绑 0 号端口拿到一个空闲端口、
/// 关掉、再用它开服务」的办法挑端口（<c>FreePort()</c>）。两个测试**并行**跑时，
/// 两边可能挑到**同一个**端口 —— 后起的那个绑不上，测试就红了，
/// 而代码一行没错（母仓 <c>HANDOFF.md</c> §27 记的就是这条）。
/// </para>
/// <para>
/// ⚠️ <b>这不是「把 flake 藏起来」，而是把那个竞态从根上拿掉</b>：
/// 端口冲突只在**同时**有两个监听器时才有。串行之后，同一个时刻只有一个。
/// </para>
/// <para>
/// ⚠️ 代价是这一族测试不再并行 —— 它们本来就要起真服务、真发请求，
/// 是全套里最慢的几个，串行之后整体会慢一点。**这个代价值得**：
/// 一条随机的红会让人开始无视红（2026-09-27 一天里它出现过三次，
/// 每次都重跑就绿 —— 而「重跑就绿」正是最坏的那种信号）。
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class HttpListenerCollection
{
    public const string Name = "http-listener";
}
