namespace VidLog.Desktop.Core.Tests;

/// <summary>
/// 碰那个**进程级密钥登记表**（<c>Sanitizer</c>）的测试**串行跑**。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有这个集合</b>：<c>Sanitizer</c> 的登记表是 <c>static</c> 的，
/// 而 <c>SanitizerTests</c> 每条用例都会 <c>ResetForTesting()</c>（清空）。
/// <c>UploadReceiverTests.签发出去的凭据会登记给脱敏层</c> 却在**注册之后**
/// 断言那串还在不在 —— 两个类并行跑时，一次清空落在「领到凭据」与「断言」中间，
/// 那条就红了，而代码一行没错。
/// </para>
/// <para>
/// ⚠️ 2026-10-01 实测撞到过一次（当时正在加实时推流那批测试，并行度变了一点点
/// 就露头了）。它是**既有**的问题：<c>SanitizerTests</c> 的类注释早就写着
/// 「单跑必过、一起跑才红」—— 只是当初只在自己类里防了，没防**别的类**。
/// </para>
/// <para>
/// ⚠️ <b>这不是「把 flake 藏起来」，而是把那个竞态从根上拿掉</b>：清空与断言
/// 只在**同时**发生时才有事。串行之后，同一个时刻只有一个在碰那张表。
/// 与 <see cref="HttpListenerCollection"/> 同一条理由、同一种做法。
/// </para>
/// <para>
/// ⚠️ 代价是这两个类不再并行。它们本来也不快（<c>UploadReceiverTests</c> 要造
/// 真文件、跑解码校验）—— **这个代价值得**：一条随机的红会让人开始无视红。
/// </para>
/// <para>
/// ⚠️ 往这里加的判据是「**它断言日志里的密钥被抹掉了**」。只登记、不断言的类
/// 不用进来（登记对它们无害）。
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class SanitizerCollection
{
    public const string Name = "sanitizer";
}
