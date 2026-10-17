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

; ── 安装前必须同意的许可协议（需求方 2026-10-17）────────────────────────
; 这是 Inno **内置**的许可页：向导第一步就是它，用户不选【我接受】时
; 「下一步」是灰的，装不下去；选【我不接受】由下面 [Code] 那一段直接退出。
;
; ⚠️ Inno 的 LicenseFile **只认一个文件**（写了多个只有最后一个生效，而且不报错）。
;    所以《服务条款》与《隐私政策》是**合成一份**的：
;      installer\legal\许可协议.txt  ← 改条款就改这个文件，它是唯一底稿
;    两份 Markdown（桌面上的初稿）只用于校对，改了 txt 之后不要再去改它们。
;
; ⚠️ 这个文件必须是 UTF-8 **带 BOM**：不带 BOM 时 Inno 按 ANSI 读，
;    许可页会整篇乱码 —— 而编译器一个字都不说。
LicenseFile=legal\许可协议.txt

; ── 向导语言 ──────────────────────────────────────────────────────────
; 不写 [Languages] 时整个安装向导是**英文**的（Inno 的默认行为），
; 而下面的任务、状态提示全是中文 —— 一份中英混排的向导。
; ⚠️ 这个 .isl 取自 Inno 官方仓库；用 6.4.3 编过探针，能编。
[Languages]
Name: "chinesesimp"; MessagesFile: "languages\ChineseSimplified.isl"

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

; ── 法律文本随程序常驻（需求方 2026-10-17）──────────────────────────────
; 许可协议在安装时已经给用户看过一遍，但那是**装之前**看的、看完就没了；
; 用户日后要回看条款、或者要看 FFmpeg 的 GPLv3 正文与源码获取方式，
; 得有一个装完之后还在的地方 —— 就是这里。
;
; ⚠️ 进 `{app}\licenses` 而不进 `{app}` 根：根目录是程序自己的地盘，
;    `{#StageDir}\*` 那一行还会整个覆盖一遍。
; ⚠️ 这里**不许**出现「安装后才生成」之类的 [Code] 逻辑：少一份文本，
;    GPLv3 的分发义务就没履行（见 第三方组件声明.txt 第一条）。
Source: "legal\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

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

; ── 关于卸载时删什么 ───────────────────────────────────────────────────
; 这里刻意**没有** [UninstallDelete] 段，而且一个字都不许加：
;
; 录像、索引、设置、日志全在 `%LOCALAPPDATA%\VidLog` 下（不在 {app} 里），
; Inno 只会删它自己装进去的那些文件，碰不到用户的录像。
; 这是母仓 AGENTS.md §3「卸载**不清用户数据**」那一条，也是本产品
; 「证据不丢」那个承诺的一部分：**卸载重装是排查故障的常规手段，
; 它绝不能变成一次数据灭失。**
;
; 需求方 2026-10-17 追加：卸载时要**问一次**要不要删，两个勾选项默认都不勾。
; 删除动作全部在下面 [Code] 里按勾选做（所以上面那条「[UninstallDelete] 一个字
; 都不许加」照旧成立 —— 那条规矩的真正意思是「不许**无条件**删」）。

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

// ── 「我不接受」= 直接退出安装（需求方 2026-10-17）──────────────────────────
//
// Inno 内置的许可页只把【下一步】灰掉：用户选了「我不接受」之后，还得再点一次
// 【取消】才走得到退出。需求方要的是**一步**。
//
// ⚠️ 实测（本机探针安装，2026-10-17）：WizardForm.Close 会弹 Inno 自己的
//    「现在退出安装程序吗？」（是/否，默认否）。这个确认**在脚本里关不掉**
//    —— 它就是【取消】按钮走的同一条退出路。留着也好：一个文件都还没写，
//    点错了重新双击安装包即可。
//    不加这个钩子的默认行为更差：选了「我不接受」之后界面**毫无反应**，
//    用户得自己猜到去点【取消】。
procedure LicenseNotAcceptedClick(Sender: TObject);
begin
  WizardForm.Close;
end;

procedure InitializeWizard;
begin
  // 许可页的控件在 InitializeWizard 时已经建好了（页面先建、这个事件后到）。
  WizardForm.LicenseNotAcceptedRadio.OnClick := @LicenseNotAcceptedClick;
end;

// ═══════════════════════════════════════════════════════════════════════════
// 卸载时按勾选删数据（需求方 2026-10-17）
//
// 需求原文：「用户卸载电脑端时，给用户选择框，供用户选择，并严格按照选择
// 结果进行卸载任务。」图上那两个框是默认**不勾**的。
//
// ⚠️ 默认不勾 = 与卸载器一直以来的行为完全一致（母仓 AGENTS.md §3
//    「卸载不清用户数据」）。也就是说：多了一个**选项**，没有改变默认结果。
//
// ⚠️ 两条永远不删的（需求方 2026-10-17 逐条裁的）：
//    ① license.json —— 它是购买凭据，不是设置。删了重装要重新激活。
//    ② trial.dat（以及 %ProgramData% 与两处注册表里的那三条）—— 试用记录。
//       清掉它们之后「卸载 → 重装」就能把 7 天试用重置一遍，等于给试用开后门。
//
// ⚠️ 只碰**本机数据目录**（%LOCALAPPDATA%\VidLog）。用户配在别的盘/移动硬盘上的
//    录像**不删** —— 那是需求方 2026-10-17 选的「只删本机数据目录里的」。
//    所以界面上必须把这条写出来，不能让用户以为勾了就全删干净了。
//
// ⚠️ 判据顺序照需求方的图：**录像全部删成功，才删录像记录（索引）**。
//    有一条删不掉（被占用 / 只读 / 盘不在）就保留索引，并当场把删不掉的列出来。
//
// ⚠️ 静默卸载（/SILENT、/VERYSILENT）不弹窗、**也不删任何数据**：
//    没有人能点勾，默认就是「都留着」。
// ═══════════════════════════════════════════════════════════════════════════

var
  DeleteSettings: Boolean;
  DeleteRecordings: Boolean;
  DeleteFailures: TStringList;
  DeleteCounter: Integer;

function DataRoot(): String;
begin
  // 与 `DataLayout.Default()` 逐字对应（%LOCALAPPDATA%\VidLog）。
  // 有测试钉着：新增一个数据文件却忘了写进下面的清单时，那条会红。
  Result := ExpandConstant('{localappdata}') + '\VidLog';
end;

procedure NoteDeleteProgress();
begin
  Inc(DeleteCounter);

  if (DeleteCounter mod 200) = 0 then
  begin
    // 删几千个文件时进度条不动会像卡死；每 200 个刷一次状态字。
    UninstallProgressForm.StatusLabel.Caption :=
      '正在删除你选择的数据…（已处理 ' + IntToStr(DeleteCounter) + ' 个文件）';
    UninstallProgressForm.Update;
  end;
end;

function RemoveOneFile(const FileName: String): Boolean;
begin
  Result := True;

  if not FileExists(FileName) then
  begin
    Exit;
  end;

  NoteDeleteProgress();

  if not DeleteFile(FileName) then
  begin
    if DeleteFailures.Count < 8 then
    begin
      DeleteFailures.Add(FileName);
    end;

    Result := False;
  end;
end;

function RemoveTree(const Dir: String): Boolean;
var
  FindRec: TFindRec;
  Item: String;
begin
  Result := True;

  if not DirExists(Dir) then
  begin
    Exit;
  end;

  if FindFirst(Dir + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Item := Dir + '\' + FindRec.Name;

          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          begin
            if not RemoveTree(Item) then
            begin
              Result := False;
            end;
          end
          else
          begin
            if not RemoveOneFile(Item) then
            begin
              Result := False;
            end;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;

  // 目录只在空了之后才删得掉；删不掉说明里面有东西留下了。
  if Result then
  begin
    if not RemoveDir(Dir) then
    begin
      if DeleteFailures.Count < 8 then
      begin
        DeleteFailures.Add(Dir + '（目录非空或删不掉）');
      end;

      Result := False;
    end;
  end;
end;

function DeleteChosenData(): Boolean;
var
  Root: String;
  RecordingsOk: Boolean;
begin
  Root := DataRoot();

  // ── 录像与录像记录 ───────────────────────────────────────────────────
  if DeleteRecordings then
  begin
    // ⚠️ 只删本机数据目录下的归档区（`StorageLocations.FallbackRoot`）。
    RecordingsOk := RemoveTree(Root + '\archive');

    // 全部删成功之后才动录像记录 —— 先删索引的话，删不掉的录像就再也没人认得了。
    if RecordingsOk then
    begin
      RecordingsOk := RemoveOneFile(Root + '\labels.jsonl');
    end;

    if RecordingsOk then
    begin
      RecordingsOk := RemoveOneFile(Root + '\punches.jsonl');
    end;

    if RecordingsOk then
    begin
      RecordingsOk := RemoveOneFile(Root + '\published.jsonl');
    end;

    if RecordingsOk then
    begin
      RecordingsOk := RemoveOneFile(Root + '\receipts.jsonl');
    end;

    // 索引**最后**删（它就是「录像记录」本身）。
    if RecordingsOk then
    begin
      RemoveOneFile(Root + '\index.jsonl');
    end;
  end;

  // ── 设置与临时文件 ───────────────────────────────────────────────────
  // ⚠️ license.json 与 trial.dat **不在这张单子里**，见上面那两条「永远不删」。
  if DeleteSettings then
  begin
    RemoveOneFile(Root + '\settings.json');
    RemoveOneFile(Root + '\calibration.json');
    RemoveOneFile(Root + '\scan-errors.jsonl');
    RemoveOneFile(Root + '\cleanup-audit.jsonl');
    RemoveOneFile(Root + '\devices.jsonl');          // 里面有手机端凭据
    RemoveOneFile(Root + '\baidu-pan-token.json');   // 网盘账号令牌
    RemoveOneFile(Root + '\upload-queue.jsonl');
    RemoveOneFile(Root + '\archive-failures.jsonl');
    RemoveTree(Root + '\logs');
    RemoveTree(Root + '\work');       // 未收尾的分段
    RemoveTree(Root + '\incoming');   // 上传暂存区
  end;

  Result := DeleteFailures.Count = 0;
end;

function AskUninstallChoices(): Boolean;
var
  Form: TSetupForm;
  Header: TNewStaticText;
  Hint: TNewStaticText;
  SettingsBox: TNewCheckBox;
  SettingsDesc: TNewStaticText;
  RecordingsBox: TNewCheckBox;
  RecordingsDesc: TNewStaticText;
  OkBtn: TNewButton;
  CancelBtn: TNewButton;
begin
  Result := False;

  Form := CreateCustomForm();

  try
    Form.Caption := '卸载 VidLog 工位录像';
    Form.BorderStyle := bsDialog;
    Form.Position := poScreenCenter;
    Form.ClientWidth := 620;
    Form.ClientHeight := 268;

    Header := TNewStaticText.Create(Form);
    Header.Parent := Form;
    Header.AutoSize := False;
    Header.Left := 24;
    Header.Top := 20;
    Header.Width := 560;
    Header.Height := 24;
    Header.Font.Size := 13;
    Header.Font.Style := [fsBold];
    Header.Caption := '卸载前，请选择要保留的内容';

    Hint := TNewStaticText.Create(Form);
    Hint.Parent := Form;
    Hint.AutoSize := False;
    Hint.WordWrap := True;
    Hint.Left := 24;
    Hint.Top := 50;
    Hint.Width := 560;
    Hint.Height := 32;
    Hint.Font.Color := clGray;
    Hint.Caption := '默认两项都不勾：设置与录像都留着，重新安装后仍能继续使用。';

    SettingsBox := TNewCheckBox.Create(Form);
    SettingsBox.Parent := Form;
    SettingsBox.Left := 24;
    SettingsBox.Top := 96;
    SettingsBox.Width := 560;
    SettingsBox.Height := 22;
    SettingsBox.Caption := '删除设置和临时文件';
    SettingsBox.Checked := False;

    SettingsDesc := TNewStaticText.Create(Form);
    SettingsDesc.Parent := Form;
    SettingsDesc.AutoSize := False;
    SettingsDesc.WordWrap := True;
    SettingsDesc.Left := 44;
    SettingsDesc.Top := 120;
    SettingsDesc.Width := 540;
    SettingsDesc.Height := 32;
    SettingsDesc.Font.Color := clGray;
    SettingsDesc.Caption := '清除设置、日志和缓存；不会删除录像与录像记录。激活记录和试用记录也保留。';

    RecordingsBox := TNewCheckBox.Create(Form);
    RecordingsBox.Parent := Form;
    RecordingsBox.Left := 24;
    RecordingsBox.Top := 160;
    RecordingsBox.Width := 560;
    RecordingsBox.Height := 22;
    RecordingsBox.Caption := '删除录像和录像记录';
    RecordingsBox.Checked := False;

    RecordingsDesc := TNewStaticText.Create(Form);
    RecordingsDesc.Parent := Form;
    RecordingsDesc.AutoSize := False;
    RecordingsDesc.WordWrap := True;
    RecordingsDesc.Left := 44;
    RecordingsDesc.Top := 184;
    RecordingsDesc.Width := 540;
    RecordingsDesc.Height := 32;
    RecordingsDesc.Font.Color := clGray;
    RecordingsDesc.Caption := '只删除本机数据目录里的录像；配在别的盘上的录像不动。删除后无法恢复。';

    OkBtn := TNewButton.Create(Form);
    OkBtn.Parent := Form;
    OkBtn.Left := 370;
    OkBtn.Top := 226;
    OkBtn.Width := 120;
    OkBtn.Height := 30;
    OkBtn.Caption := '开始卸载';
    OkBtn.Default := True;
    OkBtn.ModalResult := mrOk;

    CancelBtn := TNewButton.Create(Form);
    CancelBtn.Parent := Form;
    CancelBtn.Left := 500;
    CancelBtn.Top := 226;
    CancelBtn.Width := 100;
    CancelBtn.Height := 30;
    CancelBtn.Caption := '取消';
    CancelBtn.Cancel := True;
    CancelBtn.ModalResult := mrCancel;

    Result := Form.ShowModal = mrOk;

    if Result then
    begin
      DeleteSettings := SettingsBox.Checked;
      DeleteRecordings := RecordingsBox.Checked;
    end;
  finally
    Form.Free;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  DeleteSettings := False;
  DeleteRecordings := False;
  DeleteCounter := 0;
  DeleteFailures := TStringList.Create();

  // ⚠️ 静默卸载不弹窗、一样都不删（上面那一段注释有理由）。
  if UninstallSilent() then
  begin
    Result := True;
    Exit;
  end;

  Result := AskUninstallChoices();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  MessageText: String;
  Index: Integer;
begin
  // 在「文件还没开始删」这一刻动手：此刻 {app} 与数据目录都还在。
  if CurUninstallStep <> usUninstall then
  begin
    Exit;
  end;

  // 两个都没勾 = 什么都不做（与老版本的卸载器一模一样）。
  if not (DeleteSettings or DeleteRecordings) then
  begin
    Exit;
  end;

  if DeleteChosenData() then
  begin
    Exit;
  end;

  // 删不掉的时候必须**当面说**：用户勾了「删除」，不能让它悄悄地没删干净。
  MessageText := '下面这些没能删掉（可能正被别的程序占用，或者文件是只读的）：'
    + #13#10#13#10;

  for Index := 0 to DeleteFailures.Count - 1 do
  begin
    MessageText := MessageText + '  · ' + DeleteFailures[Index] + #13#10;
  end;

  if DeleteFailures.Count >= 8 then
  begin
    MessageText := MessageText + '  · …（只列出前 8 条）' + #13#10;
  end;

  MessageText := MessageText + #13#10
    + '它们还在原处。录像记录（索引）在你勾了「删除录像」时有删不掉的文件就整份保留，'
    + '重新安装后仍能看到它们；其余已按你的选择处理。';

  MsgBox(MessageText, mbError, MB_OK);
end;
