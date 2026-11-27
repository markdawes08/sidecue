#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$LostFocusEvent
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationFramework

$window = New-Object System.Windows.Window
$window.Title = 'Sidecue synthetic foreground target'
$window.Width = 440
$window.Height = 150
$window.WindowStartupLocation = 'CenterScreen'

$textBox = New-Object System.Windows.Controls.TextBox
$textBox.Text = 'Synthetic foreground target'
$textBox.Margin = [System.Windows.Thickness]::new(16.0)
$textBox.VerticalAlignment = 'Center'
$textBox.IsReadOnly = $true
$window.Content = $textBox

$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(24)
$timer.Add_Tick({
    $timer.Stop()
    $window.Close()
})

$lostFocusSignal = [System.Threading.EventWaitHandle]::OpenExisting($LostFocusEvent)
try {
    $window.Add_Activated({ [void]$textBox.Focus() })
    $window.Add_Deactivated({ [void]$lostFocusSignal.Set() })
    $timer.Start()
    [void]$window.ShowDialog()
} finally {
    $timer.Stop()
    $lostFocusSignal.Dispose()
}
