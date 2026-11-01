param(
    [Parameter(Mandatory = $true)][string]$InputExe,
    [Parameter(Mandatory = $true)][string]$OutputExe,
    [Parameter(Mandatory = $true)][string]$ProjectFile,
    [Parameter(Mandatory = $true)][string]$IconFile
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$inputItem = Get-Item -LiteralPath $InputExe
$projectItem = Get-Item -LiteralPath $ProjectFile
$iconItem = Get-Item -LiteralPath $IconFile
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputExe)
New-Item -ItemType Directory -Path (Split-Path -Parent $outputPath) -Force | Out-Null
[xml]$project = Get-Content -LiteralPath $projectItem.FullName -Raw
$version = [Version]$project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Could not read Version from the project file' }
$versionText = $version.ToString(3)
$versionCsv = '{0},{1},{2},0' -f $version.Major, $version.Minor, $version.Build

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw 'Visual Studio C++ x64 tools were not found' }
Import-Module (Join-Path $vsPath 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vsPath -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64'

$objectDirectory = Join-Path $root 'obj\Bootstrap'
New-Item -ItemType Directory -Path $objectDirectory -Force | Out-Null
$resourceFile = Join-Path $objectDirectory 'SylnarBootstrap.rc'
$resourceObject = Join-Path $objectDirectory 'SylnarBootstrap.res'
$sourceFile = Join-Path $PSScriptRoot 'SylnarBootstrap.cpp'
$inputRc = $inputItem.FullName.Replace('\', '/')
$iconRc = $iconItem.FullName.Replace('\', '/')
$resource = @"
#include <winver.h>
101 RCDATA "$inputRc"
1 ICON "$iconRc"
1 VERSIONINFO
FILEVERSION $versionCsv
PRODUCTVERSION $versionCsv
FILEFLAGSMASK 0x3fL
FILEFLAGS 0x0L
FILEOS 0x40004L
FILETYPE 0x1L
FILESUBTYPE 0x0L
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904b0"
    BEGIN
      VALUE "CompanyName", "Sylnar"
      VALUE "FileDescription", "FPGA DMA board management tool."
      VALUE "FileVersion", "$versionText.0"
      VALUE "InternalName", "Sylnar"
      VALUE "OriginalFilename", "Sylnar.exe"
      VALUE "ProductName", "Sylnar"
      VALUE "ProductVersion", "$versionText"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x409, 1200
  END
END
"@
Set-Content -LiteralPath $resourceFile -Value $resource -Encoding Ascii
& rc.exe /nologo /fo $resourceObject $resourceFile
if ($LASTEXITCODE) { throw "Resource compilation failed with exit code $LASTEXITCODE" }
& cl.exe /nologo /std:c++17 /EHsc /O2 /MT /W4 /WX /DUNICODE /D_UNICODE `
    $sourceFile $resourceObject /Fe:$outputPath /link /SUBSYSTEM:WINDOWS ole32.lib shell32.lib user32.lib
if ($LASTEXITCODE) { throw "Bootstrap compilation failed with exit code $LASTEXITCODE" }
