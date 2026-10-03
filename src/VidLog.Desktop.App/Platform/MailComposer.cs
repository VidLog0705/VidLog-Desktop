using System.IO;
using System.Runtime.InteropServices;
using VidLog.Desktop.Core.Diagnostics;

namespace VidLog.Desktop.App.Platform;

/// <summary>
/// 把一封**带附件的邮件**写好，交给用户机器上已经装好的邮件客户端发出去
/// （Simple MAPI：`MAPISendMail`）。
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>为什么不是程序自己发（SMTP）</b>：那要一套发信凭据（账号 + 授权码）。
/// 凭据**不能写死在程序里** —— 那等于把发信权交给每一个装了这个软件的人；
/// 而让用户自己申请一套再填进来，就把「发一份日志给售后」这件小事
/// 变成一次配置。交给系统邮件客户端没有这个问题：凭据在用户自己的客户端里，
/// 我们只替他填好收件人、标题、正文与附件，**最后那一下由他按**。
/// </para>
/// <para>
/// ⚠️ <b>失败要说得出原因</b>（I3）：机器上没装邮件客户端很常见，
/// 而它表现为「点了没反应」的话，用户只会以为程序坏了。调用方拿到那句话
/// **必须显示出来**，并告诉他第二条路（诊断包已经存在本地了，手发）。
/// </para>
/// <para>
/// ⚠️ <b>64 位程序调不到 32 位的邮件客户端</b>（Windows 的 <c>fixmapi</c>
/// 只做同位数转发）—— 那种机器上会返回 <c>MAPI_E_NOT_INITIALIZED</c>，
/// 一并归到「没找到可用的邮件客户端」。
/// </para>
/// </remarks>
internal static class MailComposer
{
    /// <summary>
    /// 把那封信交给系统邮件客户端。
    /// </summary>
    /// <returns>
    /// 成功返回 <see langword="null"/>；失败返回**一句给人看的原因**
    /// （调用方负责显示出来，不许吞掉）。
    /// </returns>
    public static string? TryCompose(string to, string subject, string body, string attachment)
    {
        if (!File.Exists(attachment))
        {
            return $"附件不在了：{attachment}";
        }

        var message = new MapiMessage { subject = subject, noteText = body };

        var files = Marshal.AllocHGlobal(Marshal.SizeOf<MapiFileDesc>());
        var recips = Marshal.AllocHGlobal(Marshal.SizeOf<MapiRecipDesc>());
        try
        {
            // ⚠️ `position = -1` 是「附件放在正文后面」；不给的话 MAPI 会
            // 把它当成正文里的一个占位符（`^0`），而那封信就没有正文了。
            Marshal.StructureToPtr(
                new MapiFileDesc
                {
                    position = -1,
                    pathName = attachment,
                    fileName = Path.GetFileName(attachment),
                },
                files, false);

            Marshal.StructureToPtr(
                new MapiRecipDesc { recipClass = MapiTo, name = to, address = to },
                recips, false);

            message.fileCount = 1;
            message.files = files;
            message.recipCount = 1;
            message.recips = recips;

            var code = MAPISendMail(IntPtr.Zero, IntPtr.Zero, message, LogonUiDialog, 0);

            return code switch
            {
                Success => null,

                // ⚠️ 用户自己把那封写好的信关掉了 —— 那不是故障，别报成故障。
                UserAbort => "邮件没有发出去（那封写好的信被关掉了）。",

                // ⚠️ 其余**一律不猜原因**，只把原样的返回码带上（它进日志，售后能查）。
                // 上一版这里给 26/27/28 各起了一个名字，而那几个号我是**推**出来的 ——
                // 猜错的错误码比没有错误码更坏：它会把「没装客户端」指到别的地方去。
                // 「可能没装客户端，或者装的是 32 位的（那种我们调不到）」是**能说清**的部分。
                _ => "没能把邮件交给邮件客户端（错误码 0x"
                     + $"{code:X8}）。这台电脑上可能没装邮件客户端，"
                     + "或者装的是 32 位的（那种我们调不到，64 位程序只能调 64 位的客户端）。"
                     + $"诊断包已经存到本地了，请手动发到 {SupportMail.Address}。",
            };
        }
        catch (DllNotFoundException)
        {
            // `MAPI32.DLL` 都没有 —— 极老的或精简过的 Windows。
            return "这台电脑上没有 MAPI（邮件接口），发不了邮件。诊断包已经存到本地了。";
        }
        catch (EntryPointNotFoundException)
        {
            return "这台电脑的邮件接口跟我们期望的不一样，发不了邮件。诊断包已经存到本地了。";
        }
        catch (Exception ex)
        {
            // ⚠️ 兜底那一条**必须留**：上面那段里有 `Marshal` 的调用，它出错时
            // 会抛到这里。不接的话调用方那边显示的是「**导出失败**」——
            // 而那时 zip **已经生成好了**，用户会以为白点了，转去重装程序。
            return $"邮件没能交给邮件客户端（{ex.Message}）。诊断包已经存到本地了。";
        }
        finally
        {
            // 先释放**结构里那些字符串**，再释放结构自己 —— 顺序反了就是泄漏。
            Marshal.DestroyStructure<MapiFileDesc>(files);
            Marshal.DestroyStructure<MapiRecipDesc>(recips);
            Marshal.FreeHGlobal(files);
            Marshal.FreeHGlobal(recips);
        }
    }

    // ── MAPI 的常量（值与 `MAPI.H` 一致）────────────────────────────────

    /// <summary>`MAPI_LOGON_UI | MAPI_DIALOG`：没有会话就先弹登录，然后把写好的信显示出来。</summary>
    private const int LogonUiDialog = 0x1 | 0x8;

    /// <summary>收件人类型：本人（`MAPI_TO`）。</summary>
    private const int MapiTo = 1;

    /// <summary>`SUCCESS_SUCCESS`。只有它表示信真的交出去了。</summary>
    private const uint Success = 0;

    /// <summary>`MAPI_USER_ABORT` —— 用户自己把那封写好的信关掉了。</summary>
    private const uint UserAbort = 1;

    // ── MAPI 那三个结构（顺序、类型必须与 `MAPI.H` 逐字对应）─────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private sealed class MapiMessage
    {
        public int reserved;
        public string? subject;
        public string? noteText;
        public string? messageType;
        public string? dateReceived;
        public string? conversationID;
        public int flags;
        public IntPtr originator;
        public int recipCount;
        public IntPtr recips;
        public int fileCount;
        public IntPtr files;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private sealed class MapiRecipDesc
    {
        public int reserved;
        public int recipClass;
        public string? name;
        public string? address;
        public int eIDSize;
        public IntPtr entryID;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private sealed class MapiFileDesc
    {
        public int reserved;
        public int flags;

        /// <summary>附件在正文里的位置；**-1 = 正文后面**（正常附件）。</summary>
        public int position;
        public string? pathName;
        public string? fileName;
        public IntPtr fileType;
    }

    // ⚠️ 返回值声明成 `uint` 不是 `int`：失败时它可能是个**带高位**的 HRESULT
    //（`fixmapi` 找不到客户端时就是这么回的），`int` 会把它变成一个负号数，
    // 而那个数印给用户看毫无意义。`uint` 配上 `X8` 格式至少是一个能拿去查的码。
    [DllImport("MAPI32.DLL", CharSet = CharSet.Ansi)]
    private static extern uint MAPISendMail(
        IntPtr session, IntPtr uiParam, MapiMessage message, int flags, int reserved);
}
