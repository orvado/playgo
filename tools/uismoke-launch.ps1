$env:DOTNET_ROOT = "C:\Program Files\dotnet"
$exe = "C:\Users\Ken\app\playgo\src\PlayGo.App\bin\Debug\net9.0-windows\PlayGo.App.exe"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Start-Process $exe -PassThru
Write-Host "PID=$($proc.Id) HasExited=$($proc.HasExited)"

# Give it a generous startup window.
$win = $null
$deadline = [DateTime]::Now.AddSeconds(12)
while (-not $win -and [DateTime]::Now -lt $deadline) {
    if ($proc.HasExited) { break }
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { Start-Sleep -Milliseconds 200 }
}

if (-not $win) {
    Write-Host "NO_WINDOW HasExited=$($proc.HasExited)"
    if (-not $proc.HasExited) { $proc.Kill() }
    exit 2
}

Write-Host "Title: $($win.Current.Name)"
Write-Host "Class: $($win.Current.ClassName)"
Write-Host "Icon: $($win.Current.HasContent.Length) BoundingRect=$($win.Current.BoundingRectangle)"

# Count descendants for a sanity check on the UI tree.
$count = ($win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)).Count
Write-Host "Descendants: $count"

# Verify a few key labels exist (Move List, Pass, New Game).
$labels = @("New Game…", "Pass", "Move List", "Open Game Record…",
            "Save Game Record", "Play Sounds", "Show Move Numbers")
foreach ($name in $labels) {
    $found = $null
    foreach ($el in ($win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition))) {
        if ($el.Current.Name -eq $name) { $found = $el; break }
    }
    Write-Host ("  label '{0}' found: {1}" -f $name, [bool]$found)
}

Start-Sleep -Milliseconds 300
if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null }
Start-Sleep -Milliseconds 800
if (-not $proc.HasExited) { $proc.Kill() }
exit 0