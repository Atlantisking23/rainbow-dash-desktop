param([string[]]$Suites = @())
$ErrorActionPreference = 'Stop'
$taskCandidate = Split-Path -Parent $PSScriptRoot
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$taskCompiler = Join-Path $taskFramework 'csc.exe'
$taskSource = Join-Path $taskCandidate 'src'
$taskTests = $PSScriptRoot
$taskCommonReferences = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll',
    (Join-Path $taskFramework 'WPF\WindowsBase.dll'), (Join-Path $taskFramework 'WPF\PresentationCore.dll'), (Join-Path $taskFramework 'WPF\PresentationFramework.dll'))
& (Join-Path $taskCandidate 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Candidate build failed.' }
New-Item -ItemType Directory -Path (Join-Path $taskCandidate '.test-output') -Force | Out-Null
$taskOutputDir = Join-Path $taskCandidate '.test-output'
$taskCases = @(
    @{ Name = 'TelemetryTests'; Main = 'TelemetryTests'; Source = @('Telemetry.cs', 'LiveQuota.cs'); Args = @() },
    @{ Name = 'LiveQuotaTests'; Main = 'LiveQuotaTests'; Source = @('Telemetry.cs', 'LiveQuota.cs'); Args = @() },
    @{ Name = 'PlaybackTests'; Main = 'NaturalPlaybackTests'; Source = @('*'); Args = @() },
    @{ Name = 'EdgeDockingTests'; Main = 'EdgeDockingTests'; Source = @('EdgeDocking.cs'); Args = @() },
    @{ Name = 'EdgeUiTests'; Main = 'EdgeUiTests'; Source = @('*'); Args = @((Join-Path $taskCandidate 'assets')) },
    @{ Name = 'CompanionPoliciesTests'; Main = 'CompanionPoliciesTests'; Source = @('*'); Args = @() },
    @{ Name = 'EnhancedUiTests'; Main = 'EnhancedUiTests'; Source = @('*'); Args = @((Join-Path $taskCandidate 'assets')) },
    @{ Name = 'RestPolicyTests'; Main = 'RestPolicyTests'; Source = @('RestPolicy.cs'); Args = @() },
    @{ Name = 'RestUiTests'; Main = 'RestUiTests'; Source = @('*'); Args = @((Join-Path $taskCandidate 'assets')) },
    @{ Name = 'SpriteAppearanceTests'; Main = 'SpriteAppearanceTests'; Source = @('*'); Args = @((Join-Path $taskCandidate 'assets')) },
    @{ Name = 'SpritePresentationTests'; Main = 'SpritePresentationTests'; Source = @('*'); Args = @((Join-Path $taskCandidate 'assets')) },
    @{ Name = 'PetControlsTests'; Main = 'PetControlsTests'; Source = @('*'); Args = @((Join-Path $taskCandidate 'assets')) },
    @{ Name = 'QuotaAndCadenceTests'; Main = 'QuotaAndCadenceTests'; Source = @('*'); Args = @((Join-Path $taskCandidate 'assets')) }
)
$taskResults = @()
foreach ($taskCase in $taskCases) {
    if ($Suites.Count -gt 0 -and $Suites -notcontains $taskCase.Name) { continue }
    $taskOutput = Join-Path $taskOutputDir ($taskCase.Name + '.exe')
    $taskArguments = @('/nologo', '/target:exe', '/codepage:65001', ('/main:' + $taskCase.Main), ('/out:' + $taskOutput))
    $taskArguments += $taskCommonReferences | ForEach-Object { '/reference:' + $_ }
    if ($taskCase.Source[0] -eq '*') { $taskArguments += Get-ChildItem -LiteralPath $taskSource -Filter '*.cs' | ForEach-Object { $_.FullName } }
    else { $taskArguments += $taskCase.Source | ForEach-Object { Join-Path $taskSource $_ } }
    $taskArguments += Join-Path $taskTests ($taskCase.Name + '.cs')
    & $taskCompiler @taskArguments
    if ($LASTEXITCODE -ne 0) { throw ('Test compile failed: ' + $taskCase.Name) }
    $taskSuiteOutput = & $taskOutput @($taskCase.Args) 2>&1
    $taskExit = $LASTEXITCODE
    $taskSuiteOutput | Set-Content -LiteralPath (Join-Path $taskOutputDir ($taskCase.Name + '.log')) -Encoding utf8
    $taskSuiteOutput | Select-Object -Last 1
    if ($taskExit -ne 0) { $taskSuiteOutput; throw ('Regression failed: ' + $taskCase.Name) }
    $taskResults += @{ suite = $taskCase.Name; exit_code = $taskExit }
}
$taskResults | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOutputDir 'regression-results.json') -Encoding utf8
