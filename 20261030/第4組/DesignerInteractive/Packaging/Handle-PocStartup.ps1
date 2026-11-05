[CmdletBinding()]
param([Parameter(Mandatory)][int]$ProcessId,[Parameter(Mandatory)][string]$ApprovedPackage,[string[]]$SupersededPackages=@())
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PocStartupPointer {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
}
'@
$process=Get-Process -Id $ProcessId
if($process.ProcessName -ne 'Revit'){throw 'Expected authorized Revit process'}
if($process.MainWindowHandle -eq 0){Write-Output 'Startup window not ready';return}
$window=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Security - Unsigned Add-In')
$dialog=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
if($dialog){
 $text=@($dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object {$_.Current.Name}) -join "`n"
 if($text.Contains('EVHub.Interactive.POC.'+$ApprovedPackage)){$action='Load Once'}
 elseif(@($SupersededPackages|Where-Object {$text.Contains('EVHub.Interactive.POC.'+$_)}).Count -eq 1){$action='Do Not Load'}
 else{throw 'Unrelated add-in load dialog; no action permitted'}
}else{
 $dialog=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Revit MCP Plugin - RevitMCP'))
 if(-not $dialog){Write-Output ('No recognized startup dialog; '+$process.MainWindowTitle);return}
 $text=@($dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)|ForEach-Object {$_.Current.Name}) -join "`n"
 if(-not $text.Contains('RevitMCP Plugin 已載入')){throw 'Unexpected MCP dialog; no action'}
 $action='Close' # Informational notice only. Never toggle the MCP service or change settings.
}
$control=$dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$action))
if(-not $control -or -not $control.Current.IsEnabled){throw 'Verified startup action unavailable'}
$r=$control.Current.BoundingRectangle
if($r.Width -lt 20 -or $r.Height -lt 10){throw 'No verified action bounds'}
[PocStartupPointer]::SetForegroundWindow($process.MainWindowHandle)|Out-Null
[PocStartupPointer]::SetCursorPos([int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2))|Out-Null
[PocStartupPointer]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
[PocStartupPointer]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Write-Output ('Clicked only verified startup action: '+$action)
