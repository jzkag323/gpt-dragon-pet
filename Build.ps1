# ============================================================================
# Build.ps1 - 零 Visual Studio / 零 .NET SDK 单文件编译脚本
# 原理：直接调用 Windows 系统内置的 .NET Framework 4.x 编译器 (csc.exe /langversion:5)
#   1. 先编译并运行 PrepareSprites.exe，完成白底抠图、亮度对齐与纯 C# 光流补帧；
#   2. 再通过 /resource: 参数将全部处理好的动画 PNG 与图标打包进单个 GPT龙娘桌宠.exe。
# ============================================================================
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..'),
    [string]$ScratchDirectory = (Join-Path $PSScriptRoot 'optimized-build')
)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework 4.x C# compiler could not be found.' }
$tempDirectory = (New-Item -ItemType Directory -Path $ScratchDirectory -Force).FullName
$outputDirectory = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$spriteTool = Join-Path $tempDirectory 'PrepareSprites.exe'
$sprites = Join-Path $tempDirectory 'sprites'

& $compiler /nologo /langversion:5 /target:exe /optimize+ /codepage:65001 "/out:$spriteTool" /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'PrepareSprites.cs')
if ($LASTEXITCODE -ne 0) { throw 'Could not compile the sprite preparation tool.' }
& $spriteTool (Join-Path $PSScriptRoot 'SourceSheet.png') $sprites
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the sprites.' }

$assemblyNames = @('System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll','System.Xml.dll','System.Web.Extensions.dll',
    'WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll')
$references = $assemblyNames | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$resourceNames = @('idle.png','blink.png','wave.png','sleep.png','pet.ico')
$animationNames = @{
    wave = 11
    stretch = 15
    curious = 7
    celebrate = 17
    'dock-left' = 7
    'dock-right' = 7
}
$resourceNames += foreach ($name in ($animationNames.Keys | Sort-Object)) { for ($i = 0; $i -lt $animationNames[$name]; $i++) { "anim-$name-$i.png" } }
$resourceNames += @('peek-left.png','peek-right.png','peek-top.png','peek-bottom.png')
$resources = $resourceNames | ForEach-Object { '/resource:' + (Join-Path $sprites $_) + ',' + $_ }
$executableName = 'GPT' + [char]0x9F99 + [char]0x5A18 + [char]0x684C + [char]0x5BA0 + '.exe'
$executable = Join-Path $outputDirectory $executableName
$icon = Join-Path $sprites 'pet.ico'
$manifest = Join-Path $PSScriptRoot 'Pet.manifest'
$sourceNames = @('PetTheme.cs','PetSettings.cs','PetWindow.cs','PetWindow.Interaction.cs','PetWindow.Docking.cs','PetWindow.Ui.cs','PetMotion.cs','AnimationClips.cs','CodexBridge.cs','CodexPanel.cs','ChatBridge.cs','ChatPanel.cs','SettingsPanel.cs')
$sources = $sourceNames | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /langversion:5 /target:winexe /optimize+ /codepage:65001 /utf8output "/out:$executable" "/win32icon:$icon" "/win32manifest:$manifest" @references @resources @sources
if ($LASTEXITCODE -ne 0) { throw 'Could not compile the desktop pet.' }
Get-Item -LiteralPath $executable
