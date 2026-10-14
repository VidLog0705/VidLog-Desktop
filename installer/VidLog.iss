; ═══════════════════════════════════════════════════════════════════════════
; VidLog 电脑端 —— 安装程序（Inno Setup 6）
;
; 编译入口是 scripts\package.ps1，它把下面几个量用 /D 传进来：
;
;   /DAppVersion=<版本>       ← 唯一来源：VidLog.Desktop.App.csproj 的 <Version>
;   /DAppVersion4=<4 段>      ← 同一个数，补成 4 段（VersionInfoVersion 只认 4 段）
;   /DStageDir=<自包含发布目录>
;   /DOutputDir=<产出目录>
;
; ⚠️ 本文件里**不许**出现任何版本号字面量，也不许出现包名字面量 ——
;    有绊线测试钉着（DesktopServicesTests.安装脚本只认传进来的版本号）。
;    改版本就改 csproj 那一行，别的什么都别改。
;
; ── 为什么需要安装程序（而不是发一个解压即用的绿色包）──────────────────
; 有两件事**只能提权做**，而两件少了都会让「手机连不上这台电脑」：
;
;   ① urlacl —— 绑 `http://+:8720/`（局域网可达的那个前缀）需要一次性注册。
;      少了它回放服务**静默**退到 `localhost`，手机连不上这台电脑
;      （2026-10-02 报上来的「点连接手机不显示 / 扫了没反应」就是这个）。
;   ② 防火墙入站放行 —— 绑的是 http.sys，**不会**弹「允许访问」那个框
;      （监听的是 System 进程），于是没人会去放行：urlacl 好了、界面正常、
;      二维码照画，**手机的请求却在防火墙这一层被丢掉**，电脑端一个字都不显示
;      （2026-10-03 报上来的「手机报连不上、电脑端不提示同意」就是这个）。
;
; 安装程序跑的时候本来就是提权的，顺手把这两件事都做掉，用户什么都不用敲。
;
; ⚠️ 必须存成 **UTF-8 带 BOM**。Inno 认不出没有 BOM 的 UTF-8，会把中文
;    按 ANSI 读 —— 界面上那一堆常量就全花了，而**编译器一个字都不说**。
;    用别的编辑器另存时选「UTF-8 with BOM / 带 BOM」，别选「UTF-8」。
;    scripts\package.ps1 察觉少了 BOM 会**当场失败**（它不替人改文件），
;    有绊线钉着（DesktopServicesTests.安装脚本是带_BOM_的_UTF8）。
; ═══════════════════════════════════════════════════════════════════════════

#ifndef AppVersion
  #error 缺 /DAppVersion=… —— 版本号只能从 csproj 读，见 AGENTS.md §10
#endif
#ifndef AppVersion4
  #error 缺 /DAppVersion4=… —— 版本号只能从 csproj 读，见 AGENTS.md §10
#endif
#ifndef StageDir
  #error 缺 /DStageDir=…（scripts\package.ps1 会传）
#endif
#ifndef OutputDir
  #error 缺 /DOutputDir=…（scripts\package.ps1 会传）
#endif
#ifndef WebView2Runtime
  #error 缺 /DWebView2Runtime=…（scripts\package.ps1 会传；见 [Code] 那一段）
#endif

[Setup]
; ⚠️ AppId 一旦发布就**再也不许改** —— 它是「同一个软件」的身份证。
;    改了之后新版本会在「程序和功能」里另起一行，旧的那行永远卸不掉。
AppId={{8C66C2FE-27CA-44C3-9D77-77A5317D399F}
AppName=VidLog 工位录像
AppVersion={#AppVersion}
AppVerName=VidLog 工位录像 {#AppVersion}
VersionInfoVersion={#AppVersion4}
AppPublisher=VidLog
AppUpdatesURL=https://github.com/VidLog0705/VidLog-Desktop/releases

; ⚠️ 必须是 admin。两个理由，缺一不可：
;   ① `{autopf}` 要落到真正的 Program Files（非提权时会落到用户目录，
;      于是同一台机器可能装出好几份）；
;   ② 下面 [Run] 里那条 urlacl 注册**只能**提权做 —— 那正是这个安装程序
;      存在的理由，装成非提权版等于把这个理由丢了。
;    非管理员账户会看到一句明确的拒绝，而不是装出一个连不上手机的半成品。
PrivilegesRequired=admin

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

DefaultDirName={autopf}\VidLog
DefaultGroupName=VidLog
DisableProgramGroupPage=yes
AllowNoIcons=yes

UninstallDisplayName=VidLog 工位录像 {#AppVersion}
UninstallDisplayIcon={app}\VidLog.Desktop.App.exe

OutputDir={#OutputDir}
OutputBaseFilename=VidLog-Desktop-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "在桌面上放一个快捷方式"; \
    GroupDescription: "附加任务："

[Files]
; 自包含发布的**整个目录**（含 tools\ffmpeg.exe）。
; ⚠️ 少了 tools\ffmpeg.exe 的那个包**能开、界面能点**，只是采集、网络摄像头
;    探测、实时多画面全都报「没有可用的 FFmpeg」—— 这种「装上了但半残」
;    比装不上更难查。package.ps1 在编译本脚本之前已经验过那一份换位置还跑得起来。
Source: "{#StageDir}\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

; ── WebView2 离线运行时（需求方 2026-10-14 要求随包）────────────────────
; ⚠️ **不进 {app} 常驻、也不进 {#StageDir}**（stage 那一行会整个目录递归拷过去）：
;    它是个一次性的安装器，装完就没用了 —— 常驻的话等于每台机器白占 200MB。
;    所以 From 指编译期那一个绝对路径，装完 `deleteafterinstall` 删掉。
;
; ⚠️ `Check` 与下面 [Run] 那条用的是**同一个**判断：这台机器上已经有了的话，
;    连拷都不拷（省一次 200MB 的磁盘写），更不会去跑它的安装器。
Source: "{#WebView2Runtime}"; DestDir: "{tmp}"; DestName: "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; \
    Flags: deleteafterinstall; Check: not IsWebView2RuntimeInstalled

[Icons]
Name: "{group}\VidLog 工位录像"; Filename: "{app}\VidLog.Desktop.App.exe"
Name: "{group}\卸载 VidLog"; Filename: "{uninstallexe}"
Name: "{autodesktop}\VidLog 工位录像"; Filename: "{app}\VidLog.Desktop.App.exe"; \
    Tasks: desktopicon

[Run]
; ── 这一次安装的全部意义 ──────────────────────────────────────────────
; 先 delete 再 add：`add` 在已存在时是非零退出，重复安装/覆盖安装会走到那一步。
; 用 `cmd /c "… & exit /b 0"` 把两条串起来并**强制返回 0** —— 注册失败不该让
; 整个安装回滚：软件本身装好了照样能用（只是手机连不上，界面上会说出来）。
; 端口 8720 与 DesktopServices.DefaultPlaybackPort 是同一个数，
; 有绊线测试比对这两处（改一处忘另一处 = 又退化成手机连不上的那个缺陷）。
Filename: "{cmd}"; \
    Parameters: "/c ""netsh http delete urlacl url=http://+:8720/ >nul 2>&1 & netsh http add urlacl url=http://+:8720/ user=Everyone >nul 2>&1 & exit /b 0"""; \
    Flags: runhidden; \
    StatusMsg: "正在登记回放地址的访问许可（少了这一步手机连不上这台电脑）…"

; user=Everyone 是**有意放宽**的：安装程序多半是管理员跑的，而真正开程序的是
; 工位上那个人，两个账户常常不是同一个。这条预留只覆盖 8720 这一个端口、
; 只在这一台机器上生效，是 HttpListener 的常规做法。
; ⚠️ 已知边界：用户在设置里把回放端口改成别的之后，这条预留就管不着新端口了。
;    那时报名窗会**看得见**地提示回退到了本机地址并给出该敲的命令（I3），
;    不是静默失效。

; ── 防火墙的入站放行（2026-10-03 补）──────────────────────────────────
; 只登记 urlacl 是不够的：绑定走的是 http.sys，Windows 那个「允许访问」的弹窗
; **不会出现**（监听的是 System 进程，没人有点的机会），于是没有人会去放行 ——
; 手机发出的请求在防火墙这一层就被丢掉，而**电脑端什么都不知道**：
; urlacl 好了、界面一切正常、二维码也照画，看着就是「扫码后没反应」。
;
; remoteip=localsubnet 是**故意**的：手机只可能从局域网连过来，只放行本网段，
; 不给整个人网开一道门。profile=any 也是**故意**的：这台机器上的网卡常常被
; 系统归到「公用网络」（本机实测就是），只给「专用」放行等于没放。
; 规则名与 Core 的 `PlaybackFirewall.RuleName` 逐字相同，有绊线钉着；
; 端口与上面 urlacl 那条是同一个数，也有绊线钉着。
Filename: "{cmd}"; \
    Parameters: "/c ""netsh advfirewall firewall delete rule name=VidLog-Playback-8720 >nul 2>&1 & netsh advfirewall firewall add rule name=VidLog-Playback-8720 dir=in action=allow protocol=TCP localport=8720 remoteip=localsubnet profile=any >nul 2>&1 & exit /b 0"""; \
    Flags: runhidden; \
    StatusMsg: "正在放行防火墙上的 8720 端口（少了这一步也连不上）…"

; ── WebView2 运行时（内嵌浏览器登录窗要它）────────────────────────────
; ⚠️ **这是「装完就能用」的那一步**：没有它，设置页里的【登录百度网盘】会弹出一句
;    「这台机器可能没装 WebView2 运行时」—— 用户自己修不了，得回来找我们。
;    Windows 11 自带；Windows 10 只在装过 Edge 或者别的带 WebView2 的软件之后才有。
;
; ⚠️ `/silent /install` 是微软给这个安装器的静默参数。它自己判断装没装、
;    装到哪一级（per-machine），**不需要我们再判一次** —— 上面那条 Check
;    只是为了省掉一次没必要的 200MB 拷贝。
; ⚠️ 不加 `nowait`：要等它装完再往下走（下面那条才是启动主程序）。
;    它自己的退出码非 0 不代表装坏了（比如「已经是最新的」），所以这里不看退出码 ——
;    真装不上时，登录窗那一刻会说出人话。
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; \
    Parameters: "/silent /install"; \
    Flags: runhidden; Check: not IsWebView2RuntimeInstalled; \
    StatusMsg: "正在装内置浏览器运行时（百度网盘登录窗要它，约一分钟）…"

Filename: "{app}\VidLog.Desktop.App.exe"; Description: "现在启动 VidLog"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
; 装的时候登记了，卸的时候就得撤掉 —— 不然那台机器上会留一条指向
; 已经卸掉的软件的预留，而且下一个装别的东西占 8720 的人会莫名失败。
Filename: "{cmd}"; \
    Parameters: "/c ""netsh http delete urlacl url=http://+:8720/ >nul 2>&1 & exit /b 0"""; \
    Flags: runhidden; RunOnceId: "RemovePlaybackUrlAcl"

; 防火墙那条规则同理：卸了还留着，就等于这台机器上一直开着一道
; 没人认领的入站口子（而且那条规则的「程序」那栏是空的，只认端口）。
Filename: "{cmd}"; \
    Parameters: "/c ""netsh advfirewall firewall delete rule name=VidLog-Playback-8720 >nul 2>&1 & exit /b 0"""; \
    Flags: runhidden; RunOnceId: "RemovePlaybackFirewallRule"

; ── 关于卸载时**不删**什么 ─────────────────────────────────────────────
; 这里刻意**没有** [UninstallDelete] 段，而且一个字都不许加：
;
; 录像、索引、设置、日志全在 `%LOCALAPPDATA%\VidLog` 下（不在 {app} 里），
; Inno 只会删它自己装进去的那些文件，碰不到用户的录像。
; 这是母仓 AGENTS.md §3「卸载**不清用户数据**」那一条，也是本产品
; 「证据不丢」那个承诺的一部分：**卸载重装是排查故障的常规手段，
; 它绝不能变成一次数据灭失。**

[Code]
// ── 这台机器上有没有 WebView2 运行时 ────────────────────────────────────────
//
// 上面 [Files] 与 [Run] 两条都挂在这个判断上：已经有了就一步都不做。
//
// ⚠️ **必须看两个地方**：per-machine（HKLM）与 **per-user**（HKCU）。
//    用户级安装完全合法（Edge 自己就是这么装的），只查 HKLM 会把
//    「他早就装过了」判成没装 —— 白白拷 200MB 再跑一遍安装器。
//    ⚠️ HKLM 那一支走 **HKLM32**：Edge Update 是 32 位的，在 64 位系统上
//    它写的是 `SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\...`，而 Inno 的
//    `HKLM32` 指的正是那一支（本安装程序是 x64compatible，不必再兜 32 位系统）。
//
// ⚠️ 那个 GUID 是 **WebView2 运行时**在 Edge Update 里的客户端 ID，不是本程序的
//    （本程序的 AppId 在最上面）。它写错的表现是「判断恒为假」= 每次都白装一遍，
//    而**别的什么都不会发生** —— 所以它有个测试钉着，别手改。
function HasRealVersion(const Value: String): Boolean;
var
  Rest: String;
begin
  // ⚠️ 判据是「这个版本串里**还有非零的数字**」，不是「不等于某个占位值」：
  //    Edge Update 在「项在、但没真装上」时会写一个全是 0 的版本。
  //    那种情况要算**没装**（算成装了的后果正是这次要防的那件事 —— 装完还是打不开登录窗）。
  //    去掉所有的 0 与点之后还剩东西，才算真有一个版本。
  //    ⚠️ 用 `StringChangeEx` 而不是 `StringReplace` —— 后者这个版本的 Inno
  //    根本没有（试过，编译报 Unknown identifier）。
  Rest := Value;
  StringChangeEx(Rest, '0', '', True);
  StringChangeEx(Rest, '.', '', True);
  Result := Rest <> '';
end;

function IsWebView2RuntimeInstalled: Boolean;
var
  Pv: String;
begin
  Result :=
    (RegQueryStringValue(HKLM32,
      'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
      'pv', Pv) and HasRealVersion(Pv))
    or
    (RegQueryStringValue(HKCU,
      'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
      'pv', Pv) and HasRealVersion(Pv));
end;
