param([switch]$Force)
$ErrorActionPreference = 'Continue'
$env:DOTNET_NOLOGO = '1'
$extra = @(); if ($Force) { $extra += '--force' }
& dotnet run --project (Join-Path $PSScriptRoot '../src/Flipnote.Bootstrap/Flipnote.Bootstrap.csproj') -c Release --verbosity quiet -- tool @extra
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
