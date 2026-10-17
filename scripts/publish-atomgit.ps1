<#
  把出好的安装程序发到 AtomGit 公开仓（用户实际下载的那个源）。

  用法：
    $env:ATOMGIT_TOKEN = '<访问令牌（经典），要项目读写>'
    pwsh -File scripts/publish-atomgit.ps1                      # 取 dist 下最新的 *-setup.exe
    pwsh -File scripts/publish-atomgit.ps1 -Path D:\x\某个.exe   # 指定文件

  目标仓：Vidlog/vidlog-releases（**公开**，只放安装程序附件，不放源码）。

  ⚠️ 为什么不放进 GitHub Actions（需求方 2026-10-17 拍板改走本机）：
     GitHub 托管 runner 在境外，直接往 AtomGit 传实测 **0.04 MB/s**
     （8 MB 传了 200 秒），300 MB 要两小时，必然撞 30 分钟超时。
     本机同一条链路上传是 4.5 MB/s（314 MB / 73 秒）。下载侧不受影响，
     用户从 AtomGit 拉是 7.76 MB/s。

  ⚠️ 平台细节：
     - 只认 `api.atomgit.com`；`web-api.atomgit.com` 套着 CloudWAF，脚本直连会被 418/403。
     - 附件**不支持同名覆盖**，重复发同一个版本号要先按 id 删掉旧的。
     - 上传是两步法：先要签名 URL（`upload_url?file_name=…`），再带平台给的头 PUT。
#>
[CmdletBinding()]
param(
    [string]$Path,
    [string]$Repository = 'Vidlog/vidlog-releases'
)

$ErrorActionPreference = 'Stop'

if (-not $env:ATOMGIT_TOKEN) {
    throw '缺环境变量 ATOMGIT_TOKEN（AtomGit「访问令牌（经典）」，权限要项目读写）'
}

if (-not $Path) {
    $Path = Get-ChildItem (Join-Path $PSScriptRoot '../dist') -Filter '*-setup.exe' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Path -or -not (Test-Path $Path)) { throw '没找到安装程序（dist 下没有 *-setup.exe，也没给 -Path）' }

$file = Get-Item $Path
$version = if ($file.BaseName -match '\d+\.\d+\.\d+') { $Matches[0] } else { $null }
if (-not $version) { throw "从 $($file.Name) 里取不到版本号" }
$tag = "v$version"

$api = "https://api.atomgit.com/api/v5/repos/$Repository"
$h   = @{ 'PRIVATE-TOKEN' = $env:ATOMGIT_TOKEN }

Write-Host "文件：$($file.FullName)（$([math]::Round($file.Length / 1MB, 1)) MB）"
Write-Host "目标：$Repository  $tag"

# 1) Release 有就复用、没有就建（404 是预期的一条路）
$release = $null
try {
    $release = Invoke-RestMethod -Uri "$api/releases/tags/$tag" -Headers $h -TimeoutSec 30
} catch {
    if ([int]$_.Exception.Response.StatusCode -ne 404) { throw }
}
if (-not $release) {
    $body = @{
        tag_name         = $tag
        target_commitish = 'main'
        name             = "VidLog $version"
        body             = 'VidLog 电脑端安装程序（自包含，带 WebView2 运行时）'
        prerelease       = $false
    } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Post -Uri "$api/releases" -Headers $h `
        -ContentType 'application/json' -Body $body -TimeoutSec 30
    Write-Host "新建 Release $tag"
}

# 2) 同名附件先删（平台不支持覆盖）
foreach ($a in @($release.assets) | Where-Object { $_.type -eq 'attach' -and $_.name -eq $file.Name }) {
    Invoke-RestMethod -Method Delete -Uri "$api/releases/$tag/attach_files/$($a.id)" -Headers $h -TimeoutSec 30
    Write-Host "删掉旧附件 id=$($a.id)"
}

# 3) 两步法上传
$esc  = [uri]::EscapeDataString($file.Name)
$info = Invoke-RestMethod -Uri "$api/releases/$tag/upload_url?file_name=$esc" -Headers $h -TimeoutSec 30
if (-not $info.url) { throw "没拿到 upload_url：$($info | ConvertTo-Json -Compress)" }

$uh = @{}
$info.headers.PSObject.Properties | ForEach-Object { $uh[$_.Name] = $_.Value }

$sw = [Diagnostics.Stopwatch]::StartNew()
Invoke-WebRequest -Method Put -Uri $info.url -Headers $uh -InFile $file.FullName -TimeoutSec 1800 | Out-Null
$sw.Stop()
Write-Host ("上传完成：{0:N1} 秒" -f $sw.Elapsed.TotalSeconds)

# 4) 复核：HTTP 200 不等于附件挂上了
$check = Invoke-RestMethod -Uri "$api/releases/tags/$tag" -Headers $h -TimeoutSec 30
if (-not (@($check.assets) | Where-Object { $_.name -eq $file.Name })) {
    throw "上传后附件列表里没有 $($file.Name)"
}

Write-Host "下载页：https://atomgit.com/$Repository/releases/tag/$tag"
Write-Host "直链：  https://atomgit.com/$Repository/releases/download/$tag/$($file.Name)"
