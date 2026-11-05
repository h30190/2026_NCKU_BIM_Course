param([Parameter(Mandatory)][string]$PackageDirectory,[Parameter(Mandatory)][string]$ReportPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Reflection.Metadata
$package=(Resolve-Path -LiteralPath $PackageDirectory).Path
$settings=Get-Content -LiteralPath (Join-Path $package 'candidate-settings.json') -Raw | ConvertFrom-Json
$assembly=Join-Path $package ("EVCanopyDesigner.Poc2.Revit.Bootstrap."+$settings.PackageId+'.dll')
$stream=[IO.File]::OpenRead($assembly)
$pe=[System.Reflection.PortableExecutable.PEReader]::new($stream)
try {
 $metadata=[System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
 $classes=@{}; $startupBytes=$null; $row=0
 foreach($handle in $metadata.TypeDefinitions) {
  $row++; $type=$metadata.GetTypeDefinition($handle); $name=$metadata.GetString($type.Name)
  if($name -in @('NewDisposableAvailability','GenerateStaticSiteAvailability')) {
   if(($type.Attributes -band [Reflection.TypeAttributes]::VisibilityMask) -ne [Reflection.TypeAttributes]::Public) { throw "Availability class is not public: $name" }
   $interfaces=@($type.GetInterfaceImplementations() | ForEach-Object {
    $implementation=$metadata.GetInterfaceImplementation($_)
    $interfaceHandle=[System.Reflection.Metadata.TypeReferenceHandle]$implementation.Interface
    $metadata.GetString($metadata.GetTypeReference($interfaceHandle).Name)
   })
   if('IExternalCommandAvailability' -notin $interfaces) { throw "Availability interface missing: $name" }
   $classes[$name]=0x02000000+$row
  }
  if($name -eq 'Poc2Application') {
   foreach($methodHandle in $type.GetMethods()) {
    $method=$metadata.GetMethodDefinition($methodHandle)
    if($metadata.GetString($method.Name) -eq 'OnStartup') { $startupBytes=[System.Reflection.Metadata.PEReaderExtensions]::GetMethodBody($pe,$method.RelativeVirtualAddress).GetILBytes() }
   }
  }
 }
 if($classes.Count -ne 2 -or !$startupBytes) { throw 'Expected compiled availability classes/OnStartup missing' }
 $startupHex=[Convert]::ToHexString([byte[]]$startupBytes)
 foreach($name in $classes.Keys) {
  $token='D0'+[Convert]::ToHexString([BitConverter]::GetBytes([int]$classes[$name]))
  if(!$startupHex.Contains($token)) { throw "OnStartup does not reference availability type: $name" }
 }
 $row=0; $setterCalls=0
 foreach($handle in $metadata.MemberReferences) {
  $row++; $member=$metadata.GetMemberReference($handle)
  if($metadata.GetString($member.Name) -eq 'set_AvailabilityClassName') {
   $token='6F'+[Convert]::ToHexString([BitConverter]::GetBytes([int](0x0A000000+$row)))
   $setterCalls += [regex]::Matches($startupHex,$token).Count
  }
 }
 if($setterCalls -ne 2) { throw 'Expected two compiled AvailabilityClassName setter calls in OnStartup' }
 [pscustomobject]@{Status='PASS';PackageId=$settings.PackageId;Assembly=$assembly;PublicAvailabilityClasses=@($classes.Keys | Sort-Object);AvailabilitySetterCalls=$setterCalls;Scope='Compiled metadata and OnStartup IL inspection only; no Revit API assembly load, commands, models or deployment';RuntimeUiBehaviorVerified=$false} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $ReportPath -Encoding utf8NoBOM
 Write-Output 'PASS: both public availability interfaces and both OnStartup registrations verified in compiled bootstrap; no Revit API execution'
} finally { $pe.Dispose(); $stream.Dispose() }
