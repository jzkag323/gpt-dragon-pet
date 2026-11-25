# ============================================================================
# Check.ps1 - 自动化集成与回归测试入口
# 原理：基于构建好的 sprites 目录将全部源码与 PetChecks.cs 编译为测试程序，
# 自动校验动画时长、贴边探头、AI 预设、设置中心与未安装 Codex 自动隐藏等 90+ 项断言。
# ============================================================================
param([string]$LiveThread, [switch]$NativeInput, [string]$ScratchDirectory = (Join-Path $PSScriptRoot 'optimized-build'))
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$refs = @('System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll','System.Xml.dll','System.Web.Extensions.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$resDir = Join-Path $ScratchDirectory 'sprites'
$names = @('idle.png','blink.png','wave.png','sleep.png','pet.ico') + @(Get-ChildItem -LiteralPath $resDir -Filter 'anim-*.png' -File | Select-Object -ExpandProperty Name) +
    @('peek-left.png','peek-right.png','peek-top.png','peek-bottom.png')
$resources = $names | ForEach-Object { '/resource:' + (Join-Path $resDir $_) + ',' + $_ }
$sources = @('PetTheme.cs','PetSettings.cs','PetWindow.cs','PetWindow.Interaction.cs','PetWindow.Docking.cs','PetWindow.Ui.cs','PetMotion.cs','AnimationClips.cs','CodexBridge.cs','CodexPanel.cs','ChatBridge.cs','ChatPanel.cs','SettingsPanel.cs','PetChecks.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$exe = Join-Path $ScratchDirectory 'PetChecks.exe'
& (Join-Path $framework 'csc.exe') /nologo /langversion:5 /target:exe /codepage:65001 /utf8output /main:PetChecks "/out:$exe" @refs @resources @sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$checkArgs = @((Join-Path $PSScriptRoot 'qa'))
if ($LiveThread) { $checkArgs += $LiveThread }
if ($NativeInput) { $checkArgs += '--native' }
& $exe @checkArgs
exit $LASTEXITCODE
