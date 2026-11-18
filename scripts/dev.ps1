param([ValidateRange(1024,65535)][int]$Port=18080)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!(Test-Path "$root/src/web/dist/index.html")){throw '请先运行 ./scripts/build.ps1'}
$go=Join-Path $env:ProgramFiles 'Go\bin\go.exe';if(!(Test-Path $go)){$go=(Get-Command go -ErrorAction Stop).Source}
$env:LMT_WEB_DIR=Join-Path $root 'src/web/dist'
$env:LMT_ADDR="127.0.0.1:$Port"
$env:LMT_PUBLIC_URL="http://localhost:$Port"
Push-Location "$root/src/server"
try{ & $go run .; if($LASTEXITCODE -ne 0){throw '服务端启动失败。'} }finally{Pop-Location}
