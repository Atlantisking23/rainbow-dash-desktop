$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $taskFramework 'csc.exe'))) {
    $taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$taskCompiler = Join-Path $taskFramework 'csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'Windows .NET Framework compiler was not found.' }
$taskReferences = @(
    'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
    'System.Web.Extensions.dll', 'System.Xaml.dll',
    (Join-Path $taskFramework 'WPF\WindowsBase.dll'),
    (Join-Path $taskFramework 'WPF\PresentationCore.dll'),
    (Join-Path $taskFramework 'WPF\PresentationFramework.dll')
)
$taskArguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001', ('/out:' + (Join-Path $taskRoot 'RainbowDash.exe')))
$taskArguments += $taskReferences | ForEach-Object { '/reference:' + $_ }
$taskIcon = Join-Path $taskRoot 'assets\rainbow.ico'
if (Test-Path -LiteralPath $taskIcon) { $taskArguments += '/win32icon:' + $taskIcon }
$taskArguments += Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
& $taskCompiler @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output ('Built: ' + (Join-Path $taskRoot 'RainbowDash.exe'))
