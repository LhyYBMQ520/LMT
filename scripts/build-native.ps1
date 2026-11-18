param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if(!(Test-Path -LiteralPath $vswhere)){throw 'Visual Studio 2026 C++ desktop tools and Windows SDK are required.'}
$vs=& $vswhere -latest -version '[18.0,19.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$vs){throw 'Install the Visual Studio 2026 Desktop development with C++ workload (v145) and Windows SDK.'}
$msbuild=Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
& $msbuild (Join-Path $root 'src/desktop/Native/LmtCapture.vcxproj') "/p:Configuration=$Configuration" '/p:Platform=x64' '/nologo' '/v:minimal'
if($LASTEXITCODE -ne 0){throw "Native audio capture build failed ($LASTEXITCODE)."}
