$ErrorActionPreference = 'Continue'
$env:DOTNET_NOLOGO = '1'
& dotnet run --project (Join-Path $PSScriptRoot 'src/Flipnote.Bootstrap/Flipnote.Bootstrap.csproj') -c Release --verbosity quiet -- note @args
exit $LASTEXITCODE
