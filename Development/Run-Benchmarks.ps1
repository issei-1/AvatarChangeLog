param(
    [Parameter(Mandatory=$true)][string]$BaselineSource,
    [string]$UnityEditor = 'C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor'
)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $PSScriptRoot ('work\benchmark-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$data = Join-Path $UnityEditor 'Data'
$dotnet = Join-Path $data 'NetCoreRuntime\dotnet.exe'
$compiler = Join-Path $data 'DotNetSdkRoslyn\csc.dll'
$references = @('-nologo','-langversion:9.0','-nostdlib+','-optimize+','-target:exe')
$references += Get-ChildItem (Join-Path $data 'NetStandard\ref\2.1.0') -Filter '*.dll' | ForEach-Object { '-r:"' + $_.FullName + '"' }
$references += '-r:"' + (Join-Path $data 'Managed\UnityEngine\UnityEngine.CoreModule.dll') + '"'
$runtime = (Get-ChildItem (Join-Path $data 'NetCoreRuntime\shared\Microsoft.NETCore.App') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
foreach ($version in @('baseline','current')) {
    $root = if ($version -eq 'baseline') { $BaselineSource } else { Join-Path $sourceRoot 'Assets\AvatarChangeLog\Editor' }
    $compilerArgs = $references + @('-out:"' + (Join-Path $runRoot ($version + '.dll')) + '"')
    $compilerArgs += @('HistoryModel.cs','MaterialLabels.cs') | ForEach-Object { '"' + (Join-Path $root $_) + '"' }
    $compilerArgs += '"' + (Join-Path $PSScriptRoot 'Benchmarks.cs') + '"'
    $rsp = Join-Path $runRoot ($version + '.rsp')
    Set-Content -LiteralPath $rsp -Value $compilerArgs -Encoding utf8
    & $dotnet $compiler ('@' + $rsp)
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark compile failed' }
    @{ runtimeOptions = @{ tfm = 'net6.0'; framework = @{ name = 'Microsoft.NETCore.App'; version = $runtime } } } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot ($version + '.runtimeconfig.json')) -Encoding utf8
}
Copy-Item -LiteralPath (Join-Path $data 'Managed\UnityEngine\UnityEngine.CoreModule.dll') -Destination $runRoot
foreach ($version in @('baseline','current')) {
    Write-Host $version
    & $dotnet (Join-Path $runRoot ($version + '.dll')) | Tee-Object -FilePath (Join-Path $runRoot ($version + '.csv'))
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark failed' }
}
Write-Host "Benchmark outputs: $runRoot"
