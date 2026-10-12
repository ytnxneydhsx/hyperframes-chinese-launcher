param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
dotnet publish "$PSScriptRoot/HyperFramesChinese.csproj" -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "$PSScriptRoot/../dist/windows/$Runtime"
if ($LASTEXITCODE -ne 0) { throw 'Windows 启动器构建失败' }
