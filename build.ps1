param([Parameter(Mandatory=$true,Position=0)][string]$InputCia,[string]$Output='artifacts/Flipnote-v7.2.0_zh-CN.cia')
& (Join-Path $PSScriptRoot 'note.ps1') build --input ([IO.Path]::GetFullPath($InputCia)) --output $Output
exit $LASTEXITCODE
