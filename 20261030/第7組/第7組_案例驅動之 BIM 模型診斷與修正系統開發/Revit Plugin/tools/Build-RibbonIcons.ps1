param([string]$PreviewPath = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$caseGraphRoot = Split-Path -Parent $PSScriptRoot
$caseGraphRibbon = @(Get-ChildItem -LiteralPath (Join-Path $caseGraphRoot 'CaseBase.extension') -Directory -Filter '*.tab')
if ($caseGraphRibbon.Count -ne 1) { throw 'Expected one ribbon tab.' }
$caseGraphRibbon = $caseGraphRibbon[0].FullName
$caseGraphIcons = @(
    @{ Bundle = 'CaseGraph.pushbutton'; Glyph = 'home' },
    @{ Bundle = 'SimilarElements.pushbutton'; Glyph = 'similar' },
    @{ Bundle = 'CompareElements.pushbutton'; Glyph = 'compare' },
    @{ Bundle = 'Relationships.pushbutton'; Glyph = 'graph' },
    @{ Bundle = 'ModelChecks.pushbutton'; Glyph = 'checks' },
    @{ Bundle = 'ShaftChecker.pushbutton'; Glyph = 'shaft' },
    @{ Bundle = 'FireCompartment.smartbutton'; Glyph = 'fire'; Disabled = $true },
    @{ Bundle = 'Egress.smartbutton'; Glyph = 'egress'; Disabled = $true },
    @{ Bundle = 'Accessibility.smartbutton'; Glyph = 'access'; Disabled = $true },
    @{ Bundle = 'StairCompliance.smartbutton'; Glyph = 'stairs'; Disabled = $true },
    @{ Bundle = 'RelatedCases.pushbutton'; Glyph = 'related' },
    @{ Bundle = 'CaseLibrary.pushbutton'; Glyph = 'library' },
    @{ Bundle = 'Report Issue.pushbutton'; Glyph = 'issue' },
    @{ Bundle = 'ReviewSolution.pushbutton'; Glyph = 'review' },
    @{ Bundle = 'SuggestFix.pushbutton'; Glyph = 'suggest' }
)
foreach ($caseGraphIcon in $caseGraphIcons) {
    $caseGraphBundles = @(Get-ChildItem -LiteralPath $caseGraphRibbon -Directory | ForEach-Object {
        $candidate = Join-Path $_.FullName $caseGraphIcon.Bundle
        if (Test-Path -LiteralPath $candidate -PathType Container) { $candidate }
    })
    if ($caseGraphBundles.Count -ne 1) { throw ('Expected one ribbon bundle: ' + $caseGraphIcon.Bundle) }
    foreach ($caseGraphDark in @($false, $true)) {
        $bitmap = New-Object System.Drawing.Bitmap 64, 64
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform(2, 2)
        $ink = [Drawing.ColorTranslator]::FromHtml($(if ($caseGraphDark) { '#B8E4ED' } else { '#176A7A' }))
        $fill = [Drawing.ColorTranslator]::FromHtml($(if ($caseGraphDark) { '#284653' } else { '#E7F3F7' }))
        if ($caseGraphIcon.Disabled) {
            $ink = [Drawing.ColorTranslator]::FromHtml($(if ($caseGraphDark) { '#7B858D' } else { '#9CA3A9' }))
            $fill = [Drawing.ColorTranslator]::FromHtml($(if ($caseGraphDark) { '#343B41' } else { '#ECEFF1' }))
        }
        $pen = New-Object Drawing.Pen $ink, 1.8
        $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $brush = New-Object Drawing.SolidBrush $fill
        $solid = New-Object Drawing.SolidBrush $ink
        try {
            switch ($caseGraphIcon.Glyph) {
                'home' {
                    foreach ($x in @(5,18)) { foreach ($y in @(5,18)) {
                        $graphics.FillRectangle($brush, $x, $y, 9, 9)
                        $graphics.DrawRectangle($pen, $x, $y, 9, 9)
                    } }
                }
                'similar' {
                    $graphics.FillRectangle($brush, 4, 5, 15, 19)
                    $graphics.DrawRectangle($pen, 4, 5, 15, 19)
                    $graphics.DrawLine($pen, 8, 9, 15, 9)
                    $graphics.DrawLine($pen, 8, 13, 13, 13)
                    $graphics.FillEllipse($brush, 14, 14, 11, 11)
                    $graphics.DrawEllipse($pen, 14, 14, 11, 11)
                    $graphics.DrawLine($pen, 23, 23, 28, 28)
                }
                'compare' {
                    $graphics.FillRectangle($brush, 3, 5, 10, 22)
                    $graphics.FillRectangle($brush, 19, 5, 10, 22)
                    $graphics.DrawRectangle($pen, 3, 5, 10, 22)
                    $graphics.DrawRectangle($pen, 19, 5, 10, 22)
                    $graphics.DrawLine($pen, 7, 11, 9, 11)
                    $graphics.DrawLine($pen, 23, 11, 25, 11)
                    $graphics.DrawLine($pen, 10, 19, 22, 19)
                    $graphics.DrawLine($pen, 10, 19, 12, 17)
                    $graphics.DrawLine($pen, 22, 19, 20, 21)
                }
                'graph' {
                    $graphics.DrawLine($pen, 16, 8, 16, 16)
                    $graphics.DrawLine($pen, 7, 16, 25, 16)
                    $graphics.DrawLine($pen, 7, 16, 7, 23)
                    $graphics.DrawLine($pen, 25, 16, 25, 23)
                    foreach ($position in @(@(12,3),@(3,22),@(21,22))) {
                        $graphics.FillRectangle($brush, $position[0], $position[1], 8, 7)
                        $graphics.DrawRectangle($pen, $position[0], $position[1], 8, 7)
                    }
                }
                'checks' {
                    # Intersecting model blocks, with a clash marker.
                    $graphics.FillRectangle($brush, 3, 4, 17, 17)
                    $graphics.DrawRectangle($pen, 3, 4, 17, 17)
                    $graphics.DrawLine($pen, 3, 9, 15, 9)
                    $graphics.DrawLine($pen, 15, 9, 20, 4)
                    $graphics.DrawLine($pen, 15, 9, 15, 21)
                    $graphics.FillRectangle($brush, 13, 14, 16, 15)
                    $graphics.DrawRectangle($pen, 13, 14, 16, 15)
                    $graphics.DrawLine($pen, 18, 19, 24, 25)
                    $graphics.DrawLine($pen, 24, 19, 18, 25)
                }
                'shaft' {
                    # A service shaft with two pipe runs and an inspection check.
                    $graphics.FillRectangle($brush, 4, 3, 22, 26)
                    $graphics.DrawRectangle($pen, 4, 3, 22, 26)
                    $graphics.DrawLine($pen, 10, 7, 10, 25)
                    $graphics.DrawLine($pen, 16, 7, 16, 16)
                    $graphics.DrawLine($pen, 16, 16, 22, 16)
                    $graphics.DrawLine($pen, 8, 12, 12, 12)
                    $graphics.DrawLine($pen, 8, 21, 12, 21)
                    $graphics.FillEllipse($brush, 18, 19, 13, 13)
                    $graphics.DrawLine($pen, 20, 25, 23, 28)
                    $graphics.DrawLine($pen, 23, 28, 29, 21)
                }
                'fire' {
                    # Compartment boundary and flame, separate from the shaft glyph.
                    $graphics.DrawRectangle($pen, 3, 4, 26, 25)
                    $graphics.DrawLine($pen, 3, 12, 12, 12)
                    $graphics.DrawLine($pen, 3, 21, 12, 21)
                    $graphics.DrawLine($pen, 8, 4, 8, 12)
                    $graphics.DrawLine($pen, 8, 21, 8, 29)
                    $flame = New-Object Drawing.Drawing2D.GraphicsPath
                    $flame.AddBezier(20, 6, 22, 12, 30, 16, 25, 23)
                    $flame.AddBezier(25, 23, 20, 29, 12, 22, 15, 16)
                    $flame.AddBezier(15, 16, 16, 19, 18, 19, 18, 15)
                    $flame.AddBezier(18, 15, 17, 11, 19, 9, 20, 6)
                    $graphics.FillPath($brush, $flame)
                    $graphics.DrawPath($pen, $flame)
                    $flame.Dispose()
                }
                'egress' {
                    $graphics.FillRectangle($brush, 3, 4, 13, 24)
                    $graphics.DrawRectangle($pen, 3, 4, 13, 24)
                    $graphics.DrawLine($pen, 6, 28, 29, 28)
                    $graphics.FillEllipse($solid, 10, 7, 4, 4)
                    $graphics.DrawLine($pen, 12, 13, 10, 19)
                    $graphics.DrawLine($pen, 10, 19, 6, 24)
                    $graphics.DrawLine($pen, 10, 19, 16, 24)
                    $graphics.DrawLine($pen, 12, 13, 16, 16)
                    $graphics.DrawLine($pen, 18, 12, 29, 12)
                    $graphics.DrawLine($pen, 25, 8, 29, 12)
                    $graphics.DrawLine($pen, 29, 12, 25, 16)
                }
                'access' {
                    $graphics.FillEllipse($brush, 4, 14, 16, 16)
                    $graphics.DrawArc($pen, 4, 14, 16, 16, 35, 285)
                    $graphics.FillEllipse($solid, 11, 2, 5, 5)
                    $graphics.DrawLine($pen, 13, 10, 13, 20)
                    $graphics.DrawLine($pen, 13, 13, 23, 13)
                    $graphics.DrawLine($pen, 13, 20, 22, 20)
                    $graphics.DrawLine($pen, 22, 20, 26, 27)
                    $graphics.DrawLine($pen, 26, 27, 30, 25)
                }
                'stairs' {
                    $graphics.DrawLine($pen, 3, 28, 9, 28)
                    $graphics.DrawLine($pen, 9, 28, 9, 22)
                    $graphics.DrawLine($pen, 9, 22, 15, 22)
                    $graphics.DrawLine($pen, 15, 22, 15, 16)
                    $graphics.DrawLine($pen, 15, 16, 21, 16)
                    $graphics.DrawLine($pen, 21, 16, 21, 10)
                    $graphics.DrawLine($pen, 21, 10, 28, 10)
                    $graphics.DrawLine($pen, 5, 18, 18, 5)
                    $graphics.DrawLine($pen, 12, 5, 18, 5)
                    $graphics.DrawLine($pen, 18, 5, 18, 11)
                }
                'related' {
                    $graphics.FillRectangle($brush, 3, 4, 14, 18)
                    $graphics.DrawRectangle($pen, 3, 4, 14, 18)
                    $graphics.FillRectangle($brush, 15, 10, 14, 18)
                    $graphics.DrawRectangle($pen, 15, 10, 14, 18)
                    $graphics.DrawLine($pen, 7, 9, 12, 9)
                    $graphics.DrawLine($pen, 20, 16, 25, 16)
                    $graphics.DrawLine($pen, 8, 26, 12, 26)
                    $graphics.DrawLine($pen, 12, 26, 10, 24)
                }
                'library' {
                    foreach ($x in @(4,12,20)) {
                        $graphics.FillRectangle($brush, $x, 5, 6, 21)
                        $graphics.DrawRectangle($pen, $x, 5, 6, 21)
                        $graphics.DrawLine($pen, $x+2, 10, $x+4, 10)
                    }
                    $graphics.DrawLine($pen, 2, 29, 29, 29)
                }
                'issue' {
                    $graphics.FillRectangle($brush, 5, 3, 21, 26)
                    $graphics.DrawRectangle($pen, 5, 3, 21, 26)
                    $graphics.DrawLine($pen, 10, 9, 21, 9)
                    $graphics.DrawLine($pen, 10, 14, 21, 14)
                    $graphics.DrawLine($pen, 10, 19, 15, 19)
                    $graphics.DrawLine($pen, 23, 21, 23, 27)
                    $graphics.DrawLine($pen, 20, 24, 26, 24)
                }
                'review' {
                    $graphics.FillRectangle($brush, 5, 5, 22, 24)
                    $graphics.DrawRectangle($pen, 5, 5, 22, 24)
                    $graphics.FillRectangle($solid, 11, 2, 10, 6)
                    $graphics.DrawLine($pen, 10, 19, 14, 23)
                    $graphics.DrawLine($pen, 14, 23, 23, 13)
                }
                'suggest' {
                    # A wand with a sparkle: apply a recorded fix.
                    $wand = New-Object Drawing.Pen $ink, 3.2
                    $wand.StartCap = $wand.EndCap = [Drawing.Drawing2D.LineCap]::Round
                    $graphics.DrawLine($wand, 5, 27, 19, 13)
                    $wand.Dispose()
                    $graphics.FillEllipse($brush, 17, 3, 12, 12)
                    $graphics.DrawLine($pen, 23, 3, 23, 15)
                    $graphics.DrawLine($pen, 17, 9, 29, 9)
                    $graphics.FillEllipse($solid, 7, 6, 4, 4)
                    $graphics.FillEllipse($solid, 25, 21, 4, 4)
                }
            }
            $filename = if ($caseGraphDark) { 'icon.dark.png' } else { 'icon.png' }
            $bitmap.Save((Join-Path $caseGraphBundles[0] $filename), [Drawing.Imaging.ImageFormat]::Png)
        } finally {
            $solid.Dispose(); $brush.Dispose(); $pen.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
        }
    }
}
Write-Output ('Built ' + $caseGraphIcons.Count + ' ribbon icons with light and dark variants.')

if ($PreviewPath) {
    # Preview the actual bundle order, labels and assets without opening Revit.
    $ribbonPanels = @()
    foreach ($ribbonLine in (Get-Content -LiteralPath (Join-Path $caseGraphRibbon 'bundle.yaml') -Encoding UTF8)) {
        if ($ribbonLine -notmatch '^\s+- (.+)$') { continue }
        $ribbonPanelName = $Matches[1]
        $ribbonPanelPath = Join-Path $caseGraphRibbon ($ribbonPanelName + '.panel')
        $ribbonButtons = @()
        foreach ($ribbonItem in (Get-Content -LiteralPath (Join-Path $ribbonPanelPath 'bundle.yaml') -Encoding UTF8)) {
            if ($ribbonItem -notmatch '^\s+- (.+)$') { continue }
            $ribbonButtonName = $Matches[1]
            $ribbonButtonPath = @(Get-ChildItem -LiteralPath $ribbonPanelPath -Directory | Where-Object { $_.Name -in @(($ribbonButtonName + '.pushbutton'), ($ribbonButtonName + '.smartbutton')) })
            if ($ribbonButtonPath.Count -ne 1) { throw ('Missing preview button: ' + $ribbonButtonName) }
            $ribbonTitle = (Get-Content -LiteralPath (Join-Path $ribbonButtonPath[0].FullName 'bundle.yaml') -Encoding UTF8 | Where-Object { $_ -match '^title:' }) -replace '^title:\s*', ''
            $ribbonButtons += @{ Path = $ribbonButtonPath[0].FullName; Title = $ribbonTitle; Disabled = $ribbonButtonPath[0].Name.EndsWith('.smartbutton') }
        }
        $ribbonPanels += @{ Title = $ribbonPanelName; Buttons = $ribbonButtons; Width = 84 * $ribbonButtons.Count + 16 }
    }
    $ribbonWidth = [int](($ribbonPanels | ForEach-Object { $_.Width } | Measure-Object -Sum).Sum + 16)
    $ribbonPreview = New-Object Drawing.Bitmap $ribbonWidth, 134
    $ribbonCanvas = [Drawing.Graphics]::FromImage($ribbonPreview)
    $ribbonCanvas.Clear([Drawing.ColorTranslator]::FromHtml('#F1F2F3'))
    $ribbonCanvas.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $ribbonFont = New-Object Drawing.Font 'Microsoft JhengHei UI', 10
    $ribbonPanelFont = New-Object Drawing.Font 'Microsoft JhengHei UI', 10.5
    $ribbonText = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#343D43'))
    $ribbonMuted = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml('#9CA3A9'))
    $ribbonLinePen = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml('#D2D6D9'))
    $ribbonFormat = New-Object Drawing.StringFormat
    $ribbonFormat.Alignment = [Drawing.StringAlignment]::Center
    try {
        $ribbonX = 8
        foreach ($ribbonPanel in $ribbonPanels) {
            $ribbonButtonX = $ribbonX + 8
            foreach ($ribbonButton in $ribbonPanel.Buttons) {
                $ribbonImage = [Drawing.Image]::FromFile((Join-Path $ribbonButton.Path 'icon.png'))
                try { $ribbonCanvas.DrawImage($ribbonImage, ($ribbonButtonX + 22), 13, 40, 40) } finally { $ribbonImage.Dispose() }
                $ribbonBrush = if ($ribbonButton.Disabled) { $ribbonMuted } else { $ribbonText }
                $ribbonLabelRect = New-Object Drawing.RectangleF $ribbonButtonX, 60, 84, 27
                $ribbonCanvas.DrawString($ribbonButton.Title, $ribbonFont, $ribbonBrush, $ribbonLabelRect, $ribbonFormat)
                $ribbonButtonX += 84
            }
            $ribbonPanelRect = New-Object Drawing.RectangleF $ribbonX, 105, $ribbonPanel.Width, 25
            $ribbonCanvas.DrawString($ribbonPanel.Title, $ribbonPanelFont, $ribbonText, $ribbonPanelRect, $ribbonFormat)
            $ribbonX += $ribbonPanel.Width
            $ribbonCanvas.DrawLine($ribbonLinePen, $ribbonX, 8, $ribbonX, 126)
        }
        $ribbonPreview.Save([IO.Path]::GetFullPath($PreviewPath), [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $ribbonFormat.Dispose(); $ribbonLinePen.Dispose(); $ribbonMuted.Dispose(); $ribbonText.Dispose()
        $ribbonPanelFont.Dispose(); $ribbonFont.Dispose(); $ribbonCanvas.Dispose(); $ribbonPreview.Dispose()
    }
    Write-Output ('Preview: ' + $PreviewPath)
}
