$ErrorActionPreference = 'Continue'
$env:DOTNET_NOLOGO = '1'
& dotnet run --project (Join-Path $PSScriptRoot '../src/Flipnote.Bootstrap/Flipnote.Bootstrap.csproj') -c Release --verbosity quiet -- dependencies @args
if ($LASTEXITCODE -ne 0) { throw 'Submodule initialization or verification failed.' }
