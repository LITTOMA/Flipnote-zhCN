@echo off
setlocal
set DOTNET_NOLOGO=1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet run --project "%~dp0src/Flipnote.Bootstrap/Flipnote.Bootstrap.csproj" -c Release --verbosity quiet -- note %*
exit /b %errorlevel%
