# Clicks a button in the running spike App.exe window through UI Automation, so the update flow can be
# driven from a terminal. Usage: ./click.ps1 'Check + download update'
param([Parameter(Mandatory)] [string] $Button)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$app = Get-Process App | Select-Object -First 1
$root = [Windows.Automation.AutomationElement]::RootElement
$win = $root.FindFirst([Windows.Automation.TreeScope]::Children,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)))
$btn = $win.FindFirst([Windows.Automation.TreeScope]::Descendants,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $Button)))
if (-not $btn) { throw "button '$Button' not found" }
$btn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
