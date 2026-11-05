$ErrorActionPreference = 'Stop'
$policy = Join-Path (Split-Path -Parent $PSScriptRoot) 'revit-plugin\DoorAppearancePolicy.cs'
if (-not ('TemporalBim.DoorAppearancePolicy' -as [type])) { Add-Type -Path $policy }

$finishCases = @(
    @{ Text = '塑鋼門扇（白色）'; Expected = $true },
    @{ Text = '白漆木門'; Expected = $true },
    @{ Text = 'PVC WHITE panel'; Expected = $true },
    @{ Text = '實木貼皮門扇'; Expected = $false },
    @{ Text = '鋁框強化玻璃門扇'; Expected = $false },
    @{ Text = '非白色塑鋼門'; Expected = $false },
    @{ Text = '白色或灰色'; Expected = $false },
    @{ Text = '黑白色面板'; Expected = $false },
    @{ Text = 'not white'; Expected = $false },
    @{ Text = 'off-white'; Expected = $false },
    @{ Text = ''; Expected = $false }
)
foreach ($case in $finishCases) {
    if ([TemporalBim.DoorAppearancePolicy]::HasWhiteFinish($case.Text) -ne $case.Expected) {
        throw "材質色彩判斷不符合預期：$($case.Text)"
    }
}
$parameterCases = @(
    @{ Text = 'Panel Material'; Expected = $true },
    @{ Text = 'Leaf Material'; Expected = $true },
    @{ Text = '門扇材質'; Expected = $true },
    @{ Text = 'Frame Material'; Expected = $false },
    @{ Text = 'Door Handle Material'; Expected = $false },
    @{ Text = 'Panel Frame Material'; Expected = $false },
    @{ Text = '門板把手材質'; Expected = $false },
    @{ Text = ''; Expected = $false }
)
foreach ($case in $parameterCases) {
    if ([TemporalBim.DoorAppearancePolicy]::IsPanelMaterial($case.Text) -ne $case.Expected) {
        throw "門板材質範圍判斷不符合預期：$($case.Text)"
    }
}
Write-Output "PASS: $($finishCases.Count + $parameterCases.Count) appearance cases; explicit white finishes only, frame and handle excluded."
