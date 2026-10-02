<#
.SYNOPSIS
    打电脑端的安装包：自包含的 win-x64 目录 + 随包 FFmpeg → 一个 zip；
    加 -Installer 再编出一个真正的安装程序（Inno Setup）。

.DESCRIPTION
    在本脚本之前，本仓**没有出包这条路** —— 只有 `dotnet build`，产物落在
    `bin\Release\`，而那是一份**依赖开发机环境**的输出（要机器上装了
    .NET 9 桌面运行时、PATH 上有 FFmpeg）。拿它去装第二台机器必然缺东西，
    而缺的东西全是**静默**的（见下面两条）。

    两件必须随包走的东西：

      1. **运行时** —— `--self-contained`。目标机器是仓库里那种
         「装个浏览器都要人帮忙」的电脑，要求先装 .NET 9 桌面运行时是不现实的。
         代价是包大 ~70MB，值。
      2. **FFmpeg** —— 放进 `tools\ffmpeg.exe`。这是
         `Core/Media/FfmpegLocator.cs` 写明的第三条查找路径（「发布时 FFmpeg
         随包分发」），定位器**早就写好了**，缺的一直是这个脚本。
         ⚠️ 它没随包走的话，装出来的软件**能开、界面能点**，只是采集、
         网络摄像头探测、实时多画面全都报「没有可用的 FFmpeg」——
         这种「装上了但半残」比装不上更难查。

    版本号**只从** `VidLog.Desktop.App.csproj` 的 `<Version>` 读（母仓
    AGENTS.md §9.1）。⚠️ 这里**不许**再写一个版本号常量 —— 两处版本必然会走岔，
    而走岔的表现是「包名写着 0.2.0、exe 属性里是 0.1.0」，事后没人对得出来。

.PARAMETER Configuration
    默认 Release。这个脚本**故意不接受 Debug** —— 出包给谁都不该是调试版。

.PARAMETER Runtime
    默认 win-x64。本仓是 WPF，只有 Windows。

.PARAMETER FfmpegPath
    随包的 ffmpeg.exe。不给就按 `FFMPEG_EXE` 环境变量、再按 PATH 找。

.PARAMETER OutputDir
    产出目录，默认仓库根的 `dist\`（已在 .gitignore 里）。

.PARAMETER Installer
    额外编出真正的安装程序（`installer\VidLog.iss`，Inno Setup）。
    ⚠️ **只有它会注册回放地址的 urlacl** —— 那是「手机连不上这台电脑」的根因，
    而注册要管理员权限，装的时候正好有。zip 那条路照旧（解压即用，
    但局域网回放会静默退到 localhost）。

.PARAMETER IsccPath
    Inno Setup 的编译器 `ISCC.exe`。不给就按 `ISCC_EXE` 环境变量、
    再按 PATH、再按两个默认安装位置找。

.EXAMPLE
    pwsh -NoProfile -File scripts/package.ps1 -Installer
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$FfmpegPath,
    [string]$OutputDir,
    [switch]$Installer,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'src\VidLog.Desktop.App\VidLog.Desktop.App.csproj'
if (-not (Test-Path $csproj)) { throw "找不到 $csproj" }

# ─────────────────────────────────────────────
# 版本号：唯一定义处在 csproj
# ─────────────────────────────────────────────
[xml]$project = Get-Content -Raw -Encoding UTF8 $csproj
$version = @($project.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) {
    throw "$csproj 里没有 <Version> —— 出包必须有版本号（母仓 AGENTS.md §9.1）"
}

# ⚠️ 安装程序的 `VersionInfoVersion` **只认 4 段数字**（`0.2.0` 会被当场拒绝），
#    所以这里把同一个数补成 4 段。补法是机械的 —— 它仍然是**那一个**版本号，
#    不是第二处定义（绊线测试比对的就是这一点）。
#
# ⚠️ 取**数字前缀**再补零，取不到就**当场失败**：`1.0.0-rc1` 这种先导段里的
#    非数字会把「按点切分再筛数字」那种写法带进沟里（它会悄悄变成 1.0.1.0）。
#    宁可不出包，也不出一个版本号是编的的包。
if ($version -notmatch '^\d+(\.\d+){0,3}') {
    throw "<Version>$version</Version> 不是以 a[.b[.c[.d]]] 开头的版本号，补不出 4 段式"
}
$version4 = $Matches[0] -split '\.'
while ($version4.Count -lt 4) { $version4 += '0' }
$version4 = $version4 -join '.'

Write-Host "版本：$version（四段式 $version4）  运行时：$Runtime  配置：$Configuration"

# ─────────────────────────────────────────────
# FFmpeg：找不到就**当场失败**，不留一份半残的包
# ─────────────────────────────────────────────
function Resolve-Ffmpeg([string]$explicit) {
    if ($explicit) {
        if (-not (Test-Path $explicit)) { throw "指定的 FFmpeg 不存在：$explicit" }
        return (Resolve-Path $explicit).Path
    }

    if ($env:FFMPEG_EXE -and (Test-Path $env:FFMPEG_EXE)) {
        return (Resolve-Path $env:FFMPEG_EXE).Path
    }

    $onPath = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    throw @'
找不到 FFmpeg。它是随包的必需品（采集、网络摄像头探测、实时多画面都要它），
所以这里**不降级**成「打一个没有 FFmpeg 的包」—— 那种包装上去是半残的。
用 -FfmpegPath 指一个，或设 FFMPEG_EXE，或把它放进 PATH。
'@
}

$ffmpeg = Resolve-Ffmpeg $FfmpegPath
Write-Host "FFmpeg：$ffmpeg"

# ─────────────────────────────────────────────
# Inno Setup：只有 -Installer 才需要
# ─────────────────────────────────────────────
function Resolve-Iscc([string]$explicit) {
    if ($explicit) {
        if (-not (Test-Path $explicit)) { throw "指定的 ISCC 不存在：$explicit" }
        return (Resolve-Path $explicit).Path
    }

    if ($env:ISCC_EXE -and (Test-Path $env:ISCC_EXE)) {
        return (Resolve-Path $env:ISCC_EXE).Path
    }

    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # choco 默认装在这两处之一（64 位系统上是 x86 那个）。
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if (-not $base) { continue }
        $guess = Join-Path $base 'Inno Setup 6\ISCC.exe'
        if (Test-Path $guess) { return (Resolve-Path $guess).Path }
    }

    throw @'
找不到 Inno Setup 的编译器 ISCC.exe（-Installer 需要它）。
⚠️ 必须是 **6.4.3 或更早**：6.5.0 起 Inno Setup 引入商业许可，未授权的副本编译时
会印一行「Non-commercial use only」，而本产品是商业软件（理由与出处见 AGENTS.md §11）。
6.4.3：https://github.com/jrsoftware/issrc/releases/download/is-6_4_3/innosetup-6.4.3.exe
静默装：innosetup-6.4.3.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS
或用 -IsccPath 指一个，或设 ISCC_EXE，或把它放进 PATH。
'@
}

# ⚠️ 和 FFmpeg 一个道理：**先找齐再动手**。等两分钟的发布 + 打包跑完才报
#    「找不到编译器」是在浪费人的时间，也容易让人以为是打包坏了。
$iscc = if ($Installer) { Resolve-Iscc $IsccPath } else { $null }
if ($iscc) { Write-Host "Inno Setup：$iscc" }

# ─────────────────────────────────────────────
# 发布
# ─────────────────────────────────────────────
if (-not $OutputDir) { $OutputDir = Join-Path $root 'dist' }
$stage = Join-Path $OutputDir 'VidLog-Desktop-stage'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

# ⚠️ 发布**不用** `--no-build`：这一步的产物必须是这个提交编出来的，
#    不能靠上一次 build 的残留（那个可能是别的分支编的）。
& dotnet publish $csproj `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

$exe = Join-Path $stage 'VidLog.Desktop.App.exe'
if (-not (Test-Path $exe)) { throw "发布完了却没有 $exe —— 产物形状变了，下面的 zip 名与说明都要跟着改" }

# ─────────────────────────────────────────────
# 随包 FFmpeg：tools\ 那一层是 FfmpegLocator 认的路径
# ─────────────────────────────────────────────
$tools = Join-Path $stage 'tools'
New-Item -ItemType Directory -Force $tools | Out-Null
$bundled = Join-Path $tools 'ffmpeg.exe'
Copy-Item $ffmpeg $bundled -Force

# ⚠️⚠️ 这一步不是形式主义，它抓到过一次真事故（2026-10-01 第一次 CI 出包）：
#
# `choco install ffmpeg` 往 `C:\ProgramData\chocolatey\bin` 放的是一个
# **392 KB 的转发器（shim）**，真正的 ffmpeg.exe 在 `...\lib\ffmpeg\...` 下面。
# 转发器里按**相对路径**找 `..\lib\ffmpeg\tools\ffmpeg\bin\ffmpeg.exe`，
# 于是它**在装过 choco 的那台机器上跑起来完全正常** —— 「拷完之后试跑一下」
# 这种验证在 CI 上照样绿，只有在别的机器上才露馅。
# 那一版包（76 MB，比正常小了 60 MB）里的 tools\ffmpeg.exe 就是这样一份转发器。
#
# 判据因此是「**换到包里的这个位置还跑不跑得起来**」：转发器找的是它自己
# 旁边那个相对路径，而包里没有 `lib\ffmpeg\...`，必然失败；
# 真的 ffmpeg 不依赖任何邻居，换个地方照样跑。
# ⚠️ 验的是**已经拷进包里的那一份**（不是原始路径）—— 要验的就是要发出去的东西。
$probe = & $bundled -version 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $probe -notmatch 'ffmpeg version') {
    throw @"
随包的 FFmpeg 换到包内位置之后跑不起来 —— 它多半是个**转发器**（chocolatey 的
shim），不是真的 ffmpeg.exe。那份转发器拷到别的机器上什么都不是。

原始路径：$ffmpeg
换位后的输出：$($probe.Trim())

换一个真的：-FfmpegPath <真的 ffmpeg.exe>，或把真的那份放进 PATH
（chocolatey 装的话，真的在 %ChocolateyInstall%\lib\ffmpeg\ 下面）。
"@
}

# ─────────────────────────────────────────────
# 打 zip
# ─────────────────────────────────────────────
# ⚠️ 用 ZipFile 而不是 Compress-Archive：后者在这体积上慢一个数量级，
#    而且它会先把整个目录读进内存。
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

$zip = Join-Path $OutputDir "VidLog-Desktop-$version-$Runtime.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zipItem = Get-Item $zip
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()

Write-Host ''
Write-Host "zip：$($zipItem.FullName)"
Write-Host ("     大小 {0:N1} MB   sha256 {1}" -f ($zipItem.Length / 1MB), $zipHash)
Write-Host '     装法：解压到任意目录 → 运行 VidLog.Desktop.App.exe（自包含，不用先装 .NET）。'
Write-Host '     ⚠️ 别把 exe 单独拖出来 —— 旁边那一堆 dll 与 tools\ffmpeg.exe 都是它的。'
Write-Host '     ⚠️ 这条路**不注册**回放地址的访问许可，于是手机连不上这台电脑。'

# ─────────────────────────────────────────────
# 安装程序（Inno Setup）—— 只有 -Installer 才编
# ─────────────────────────────────────────────
if ($Installer) {
    $iss = Join-Path $root 'installer\VidLog.iss'
    if (-not (Test-Path $iss)) { throw "找不到 $iss" }

    # ⚠️ ISCC 的 /D 传参处理不了带空格的路径 —— 与其在编译期报一句看不懂的错，
    #    不如在这儿说清楚。
    if ($stage -match ' ' -or $OutputDir -match ' ') {
        throw "路径里有空格（stage=$stage / OutputDir=$OutputDir）—— ISCC 的 /D 传不了，换个不带空格的位置"
    }

    # ⚠️ Inno 认不出没有 BOM 的 UTF-8，会把中文按 ANSI 读 —— 装出来的向导与
    #    快捷方式名字全花，而**编译器一个字都不说**。
    #    ⚠️ 这里**不替人补 BOM**：打包脚本偷偷改一个受版本控制的工作区文件，
    #    是那种「跑完一遍 git status 里多出一处不认识改动」的坑。
    #    改成**当场失败**并说清怎么修。配套的绊线在
    #    DesktopServicesTests.安装脚本是带_BOM_的_UTF8，正常轮不到这里报。
    $issBytes = [System.IO.File]::ReadAllBytes($iss)
    if (-not ($issBytes.Length -ge 3 -and $issBytes[0] -eq 0xEF -and
              $issBytes[1] -eq 0xBB -and $issBytes[2] -eq 0xBF)) {
        throw @"
$iss 少了 UTF-8 BOM —— Inno 会把里面的中文按 ANSI 读，编出来的向导全花。
用编辑器把它另存为「UTF-8 with BOM / 带 BOM」，别用「UTF-8」。
（不要在这里自动补：那会让一次打包悄悄改动工作区里的受控文件。）
"@
    }

    Write-Host ''
    Write-Host "编安装程序：$iscc"

    # ⚠️ 先清掉旧的：下面按 `*-setup.exe` 取产物，留一个旧的在那儿会**假装成功**。
    Get-ChildItem $OutputDir -Filter '*-setup.exe' -ErrorAction SilentlyContinue | Remove-Item -Force

    & $iscc "/DAppVersion=$version" "/DAppVersion4=$version4" `
        "/DStageDir=$stage" "/DOutputDir=$OutputDir" $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败（退出码 $LASTEXITCODE）" }

    $setup = Get-ChildItem $OutputDir -Filter '*-setup.exe' | Select-Object -First 1
    if (-not $setup) { throw "ISCC 说编好了，$OutputDir 里却没有 *-setup.exe" }

    $setupHash = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash.ToLower()
    Write-Host ''
    Write-Host "安装程序：$($setup.FullName)"
    Write-Host ("     大小 {0:N1} MB   sha256 {1}" -f ($setup.Length / 1MB), $setupHash)
    Write-Host '     装到 Program Files，顺手注册 8720 端口的访问许可（所以会要提权）。'
    Write-Host '     ⚠️ 卸载**不删**录像：数据在 %LOCALAPPDATA%\VidLog，不在安装目录里。'
}

# ⚠️ stage 到这里才能删 —— 安装程序就是拿它当素材的。
Remove-Item $stage -Recurse -Force
