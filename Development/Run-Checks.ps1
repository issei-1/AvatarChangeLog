param(
    [string]$UnityEditor = 'C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor',
    [switch]$RunUnity
)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $PSScriptRoot ('work\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$unityData = Join-Path $UnityEditor 'Data'
$dotnet = Join-Path $unityData 'NetCoreRuntime\dotnet.exe'
$compiler = Join-Path $unityData 'DotNetSdkRoslyn\csc.dll'
$references = @('-nologo', '-langversion:9.0', '-nostdlib+')
$references += Get-ChildItem (Join-Path $unityData 'NetStandard\ref\2.1.0') -Filter '*.dll' | ForEach-Object { '-r:"' + $_.FullName + '"' }
$references += Get-ChildItem (Join-Path $unityData 'Managed\UnityEngine') -Filter '*.dll' | ForEach-Object { '-r:"' + $_.FullName + '"' }
$references += Get-ChildItem (Join-Path $unityData 'Managed') -Filter 'UnityEditor*.dll' | Where-Object { $_.Name -ne 'UnityEditor.dll' } | ForEach-Object { '-r:"' + $_.FullName + '"' }
$sourceFiles = Get-ChildItem (Join-Path $sourceRoot 'Assets\AvatarChangeLog\Editor') -Filter '*.cs' | ForEach-Object { '"' + $_.FullName + '"' }
function Compile-Source([string]$Name, [string[]]$Arguments) {
    $responseFile = Join-Path $runRoot ($Name + '.rsp')
    Set-Content -LiteralPath $responseFile -Value $Arguments -Encoding utf8
    & $dotnet $compiler ('@' + $responseFile)
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $Name" }
}
Compile-Source 'Production' ($references + @('-target:library', ('-out:"' + (Join-Path $runRoot 'AvatarChangeLog.Editor.dll') + '"')) + $sourceFiles)
$tests = @(
    ('"' + (Join-Path $sourceRoot 'Assets\AvatarChangeLog\Editor\AutoRecorder.cs') + '"')
    ('"' + (Join-Path $sourceRoot 'Assets\AvatarChangeLog\Editor\RecordingSettings.cs') + '"')
    ('"' + (Join-Path $sourceRoot 'Assets\AvatarChangeLog\Editor\MaterialLabels.cs') + '"')
    ('"' + (Join-Path $sourceRoot 'Assets\AvatarChangeLog\Editor\SnapshotCapture.cs') + '"')
    ('"' + (Join-Path $sourceRoot 'Assets\AvatarChangeLog\Editor\HistoryModel.cs') + '"')
    ('"' + (Join-Path $sourceRoot 'Assets\AvatarChangeLog\Editor\HistoryStore.cs') + '"')
    ('"' + (Join-Path $PSScriptRoot 'CoreTests.cs') + '"')
)
Compile-Source 'CoreTests' ($references + @('-target:exe', ('-out:"' + (Join-Path $runRoot 'CoreTests.dll') + '"')) + $tests)
Compile-Source 'IntegrationTests' ($references + @('-target:library', ('-out:"' + (Join-Path $runRoot 'IntegrationTests.dll') + '"'), ('-r:"' + (Join-Path $runRoot 'AvatarChangeLog.Editor.dll') + '"')) + @(('"' + (Join-Path $PSScriptRoot 'Fixture.cs') + '"'), ('"' + (Join-Path $PSScriptRoot 'Validation.cs') + '"')))
$runtimeVersion = (Get-ChildItem (Join-Path $unityData 'NetCoreRuntime\shared\Microsoft.NETCore.App') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
@{ runtimeOptions = @{ tfm = 'net6.0'; framework = @{ name = 'Microsoft.NETCore.App'; version = $runtimeVersion } } } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'CoreTests.runtimeconfig.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $unityData 'Managed\UnityEngine\UnityEngine.CoreModule.dll') -Destination $runRoot
Copy-Item -LiteralPath (Join-Path $unityData 'Managed\UnityEngine\UnityEngine.JSONSerializeModule.dll') -Destination $runRoot
& $dotnet (Join-Path $runRoot 'CoreTests.dll') | Tee-Object -FilePath (Join-Path $runRoot 'core-test-results.txt')
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
if ($RunUnity) {
    $project = Join-Path $runRoot 'ValidationProject'
    $reports = Join-Path $runRoot 'Reports'
    New-Item -ItemType Directory -Force -Path (Join-Path $project 'Assets\Editor'), (Join-Path $project 'Packages'), (Join-Path $project 'ProjectSettings'), $reports | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'Assets\AvatarChangeLog') -Destination (Join-Path $project 'Assets') -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Fixture.cs') -Destination (Join-Path $project 'Assets')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Validation.cs') -Destination (Join-Path $project 'Assets\Editor')
    Set-Content -LiteralPath (Join-Path $project 'Packages\manifest.json') -Value '{"dependencies":{"com.unity.modules.physics":"1.0.0","com.unity.modules.animation":"1.0.0","com.unity.modules.imgui":"1.0.0"}}' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $project 'ProjectSettings\ProjectVersion.txt') -Value 'm_EditorVersion: 2022.3.22f1' -Encoding utf8
    $previousOutput = $env:AVATAR_CHANGE_LOG_OUTPUT
    try {
        $env:AVATAR_CHANGE_LOG_OUTPUT = $reports
        foreach ($method in @('Run', 'Restart', 'Automatic', 'Polling', 'Background', 'BackgroundRestart', 'Pause', 'PauseRestart', 'AutoFix')) {
            $unityArguments = @('-batchmode', '-nographics', '-projectPath', ('"' + $project + '"'), '-executeMethod', "Validation.$method", '-logFile', ('"' + (Join-Path $runRoot ("unity-$method.log")) + '"'))
            $unityProcess = Start-Process -FilePath (Join-Path $UnityEditor 'Unity.exe') -ArgumentList $unityArguments -WindowStyle Hidden -PassThru
            if (-not $unityProcess.WaitForExit(240000)) { $unityProcess.Kill(); throw "Unity test $method timed out. Check logs in $runRoot" }
            if ($unityProcess.ExitCode -ne 0) { throw "Unity test $method failed with exit code $($unityProcess.ExitCode). Check logs in $runRoot" }
        }
    }
    finally { $env:AVATAR_CHANGE_LOG_OUTPUT = $previousOutput }
}
Write-Host "Checks completed. Results: $runRoot"
