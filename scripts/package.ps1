<#
.SYNOPSIS
    打电脑端的安装包：自包含的 win-x64 目录 + 随包 FFmpeg → 一个 zip。

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

.EXAMPLE
    pwsh -NoProfile -File scripts/package.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$FfmpegPath,
    [string]$OutputDir
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

Write-Host "版本：$version  运行时：$Runtime  配置：$Configuration"

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
Copy-Item $ffmpeg (Join-Path $tools 'ffmpeg.exe') -Force

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

Remove-Item $stage -Recurse -Force

$item = Get-Item $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()

Write-Host ''
Write-Host "打好了：$($item.FullName)"
Write-Host ("大小：{0:N1} MB" -f ($item.Length / 1MB))
Write-Host "sha256：$hash"
Write-Host ''
Write-Host '装法：解压到任意目录 → 运行 VidLog.Desktop.App.exe（自包含，不需要先装 .NET）。'
Write-Host '⚠️ 别把 exe 单独拖出来 —— 它旁边那一堆 dll 与 tools\ffmpeg.exe 都是它的。'
