[CmdletBinding()]
param([Parameter(Mandatory)][int]$ProcessId,[Parameter(Mandatory)][string]$OutputPath,[switch]$ActivePopup)
$ErrorActionPreference='Stop'
$process=Get-Process -Id $ProcessId
if($process.ProcessName -ne 'Revit'){throw 'Expected Revit POC process'}
$path=[IO.Path]::GetFullPath($OutputPath)
if(Test-Path -LiteralPath $path){throw 'Refusing to overwrite screenshot'}
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PocWindowCapture {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern IntPtr GetLastActivePopup(IntPtr window);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window,out Rect rect);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
}
'@
$captureHandle=$process.MainWindowHandle
if($ActivePopup){$captureHandle=[PocWindowCapture]::GetLastActivePopup($captureHandle)}
$rect=[PocWindowCapture+Rect]::new()
if(-not [PocWindowCapture]::GetWindowRect($captureHandle,[ref]$rect)){throw 'Revit window unavailable'}
$bitmap=[Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
$graphics=[Drawing.Graphics]::FromImage($bitmap);$dc=$graphics.GetHdc()
try{$ok=[PocWindowCapture]::PrintWindow($captureHandle,$dc,2)}finally{$graphics.ReleaseHdc($dc)}
try{if(-not $ok){throw 'PrintWindow failed'};$stream=[IO.FileStream]::new($path,[IO.FileMode]::CreateNew);try{$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)}finally{$stream.Dispose()}}finally{$graphics.Dispose();$bitmap.Dispose()}
[pscustomobject]@{ProcessId=$ProcessId;Title=$process.MainWindowTitle;Screenshot=$path}
