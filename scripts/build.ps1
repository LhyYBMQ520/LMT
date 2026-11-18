param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
function Invoke-Checked([string]$File,[string[]]$Arguments){ & $File @Arguments; if($LASTEXITCODE -ne 0){throw "$File failed ($LASTEXITCODE)"} }
$go=Join-Path $env:ProgramFiles 'Go\bin\go.exe'
if(!(Test-Path -LiteralPath $go)){$go=(Get-Command go -ErrorAction Stop).Source}
$output=Join-Path $root 'artifacts/server/linux-amd64'
# Keep cross-compilation variables scoped to this script and restore them on exit.
$goVariables=@('GOOS','GOARCH','GOAMD64','CGO_ENABLED')
$saved=@{};foreach($name in $goVariables){$saved[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
Push-Location $root
try{
    New-Item -ItemType Directory -Force "$output/web","$output/deploy","$output/licenses" | Out-Null
    Push-Location src/web
    try{Invoke-Checked 'pnpm.cmd' @('install','--frozen-lockfile');Invoke-Checked 'pnpm.cmd' @('run','build')}finally{Pop-Location}
    Push-Location src/server
    try{
        $env:GOOS='windows';$env:GOARCH='amd64';$env:GOAMD64='v1';$env:CGO_ENABLED='0'
        Invoke-Checked $go @('test','-count=1','./...')
        $env:GOOS='linux'
        Invoke-Checked $go @('build','-trimpath','-ldflags=-s -w','-o',"$output/lmt-server",'.')
    }finally{Pop-Location}
    Copy-Item src/web/dist/* "$output/web" -Recurse -Force
    Copy-Item deploy/* "$output/deploy" -Recurse -Force
    Copy-Item licenses/* "$output/licenses" -Recurse -Force
    Copy-Item README.md,THIRD_PARTY_NOTICES.md $output -Force
    $hash=(Get-FileHash -LiteralPath "$output/lmt-server" -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$output/SHA256SUMS", "$hash  lmt-server`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Debian 13 x64 server ready: $output"
    Write-Host 'Build and publish the desktop separately in Visual Studio 2026 using Windows-x64.'
}finally{
    Pop-Location
    foreach($name in $goVariables){[Environment]::SetEnvironmentVariable($name,$saved[$name],'Process')}
}
