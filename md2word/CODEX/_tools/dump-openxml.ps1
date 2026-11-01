$ErrorActionPreference = 'Stop'
$dll = Get-ChildItem -Path "$env:USERPROFILE\.nuget\packages\documentformat.openxml\3.5.1\lib" -Recurse -Filter DocumentFormat.OpenXml.dll |
    Where-Object { $_.FullName -match 'net8\.0' } | Select-Object -First 1
Write-Host "ASSEMBLY: $($dll.FullName)"
$libDir = $dll.Directory.FullName
$frameworkDir = Join-Path $env:USERPROFILE '.nuget\packages\documentformat.openxml.framework\3.5.1\lib\net8.0'
$handler = [System.ResolveEventHandler] {
    param($s, $e)
    $name = ($e.Name -split ',')[0]
    foreach ($dir in @($libDir, $frameworkDir)) {
        $candidate = Join-Path $dir "$name.dll"
        if (Test-Path $candidate) { return [System.Reflection.Assembly]::LoadFrom($candidate) }
    }
    return $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($handler)
$asm = [System.Reflection.Assembly]::LoadFrom($dll.FullName)
try { $types = $asm.GetExportedTypes() } catch { $types = $_.Exception.Types | Where-Object { $_ -ne $null } }
$byName = @{}
foreach ($t in $types) { $byName[$t.FullName] = $t }

function Check([string[]]$names) {
    foreach ($n in $names) {
        if ($byName.ContainsKey($n)) {
            Write-Host ("  OK   " + $n)
        } else {
            Write-Host ("  MISS " + $n)
        }
    }
}

Write-Host "`n--- Wordprocessing (paragraph/run) ---"
Check @(
    'DocumentFormat.OpenXml.Wordprocessing.Paragraph',
    'DocumentFormat.OpenXml.Wordprocessing.ParagraphProperties',
    'DocumentFormat.OpenXml.Wordprocessing.ParagraphStyleId',
    'DocumentFormat.OpenXml.Wordprocessing.NumberingProperties',
    'DocumentFormat.OpenXml.Wordprocessing.NumberingLevelReference',
    'DocumentFormat.OpenXml.Wordprocessing.NumberingId',
    'DocumentFormat.OpenXml.Wordprocessing.KeepNext',
    'DocumentFormat.OpenXml.Wordprocessing.KeepLines',
    'DocumentFormat.OpenXml.Wordprocessing.ContextualSpacing',
    'DocumentFormat.OpenXml.Wordprocessing.OutlineLevel',
    'DocumentFormat.OpenXml.Wordprocessing.Justification',
    'DocumentFormat.OpenXml.Wordprocessing.Indentation',
    'DocumentFormat.OpenXml.Wordprocessing.SpacingBetweenLines',
    'DocumentFormat.OpenXml.Wordprocessing.ParagraphBorders',
    'DocumentFormat.OpenXml.Wordprocessing.Shading',
    'DocumentFormat.OpenXml.Wordprocessing.Run',
    'DocumentFormat.OpenXml.Wordprocessing.RunProperties',
    'DocumentFormat.OpenXml.Wordprocessing.RunStyle',
    'DocumentFormat.OpenXml.Wordprocessing.RunFonts',
    'DocumentFormat.OpenXml.Wordprocessing.Bold',
    'DocumentFormat.OpenXml.Wordprocessing.Italic',
    'DocumentFormat.OpenXml.Wordprocessing.Strike',
    'DocumentFormat.OpenXml.Wordprocessing.Color',
    'DocumentFormat.OpenXml.Wordprocessing.FontSize',
    'DocumentFormat.OpenXml.Wordprocessing.FontSizeComplexScript',
    'DocumentFormat.OpenXml.Wordprocessing.Highlight',
    'DocumentFormat.OpenXml.Wordprocessing.Underline',
    'DocumentFormat.OpenXml.Wordprocessing.VerticalTextAlignment',
    'DocumentFormat.OpenXml.Wordprocessing.Break',
    'DocumentFormat.OpenXml.Wordprocessing.TabChar',
    'DocumentFormat.OpenXml.Wordprocessing.Text',
    'DocumentFormat.OpenXml.Wordprocessing.Hyperlink',
    'DocumentFormat.OpenXml.Wordprocessing.FootnoteReference',
    'DocumentFormat.OpenXml.Wordprocessing.NoProof'
)

Write-Host "`n--- tables ---"
Check @(
    'DocumentFormat.OpenXml.Wordprocessing.Table',
    'DocumentFormat.OpenXml.Wordprocessing.TableProperties',
    'DocumentFormat.OpenXml.Wordprocessing.TableStyle',
    'DocumentFormat.OpenXml.Wordprocessing.TableWidth',
    'DocumentFormat.OpenXml.Wordprocessing.TableBorders',
    'DocumentFormat.OpenXml.Wordprocessing.TableGrid',
    'DocumentFormat.OpenXml.Wordprocessing.GridColumn',
    'DocumentFormat.OpenXml.Wordprocessing.TableRow',
    'DocumentFormat.OpenXml.Wordprocessing.TableRowProperties',
    'DocumentFormat.OpenXml.Wordprocessing.TableHeader',
    'DocumentFormat.OpenXml.Wordprocessing.TableJustification',
    'DocumentFormat.OpenXml.Wordprocessing.TableCell',
    'DocumentFormat.OpenXml.Wordprocessing.TableCellProperties',
    'DocumentFormat.OpenXml.Wordprocessing.TableCellWidth',
    'DocumentFormat.OpenXml.Wordprocessing.GridSpan',
    'DocumentFormat.OpenXml.Wordprocessing.VerticalMerge',
    'DocumentFormat.OpenXml.Wordprocessing.TableCellMargin',
    'DocumentFormat.OpenXml.Wordprocessing.TableCellMarginDefault',
    'DocumentFormat.OpenXml.Wordprocessing.TableLayout',
    'DocumentFormat.OpenXml.Wordprocessing.TableLook',
    'DocumentFormat.OpenXml.Wordprocessing.CantSplit'
)

Write-Host "`n--- document / section / styles ---"
Check @(
    'DocumentFormat.OpenXml.Wordprocessing.Document',
    'DocumentFormat.OpenXml.Wordprocessing.Body',
    'DocumentFormat.OpenXml.Wordprocessing.SectionProperties',
    'DocumentFormat.OpenXml.Wordprocessing.PageSize',
    'DocumentFormat.OpenXml.Wordprocessing.PageMargin',
    'DocumentFormat.OpenXml.Wordprocessing.Style',
    'DocumentFormat.OpenXml.Wordprocessing.Styles',
    'DocumentFormat.OpenXml.Wordprocessing.BasedOn',
    'DocumentFormat.OpenXml.Wordprocessing.NextParagraphStyle',
    'DocumentFormat.OpenXml.Wordprocessing.StyleName',
    'DocumentFormat.OpenXml.Wordprocessing.PrimaryStyle',
    'DocumentFormat.OpenXml.Wordprocessing.StyleParagraphProperties',
    'DocumentFormat.OpenXml.Wordprocessing.StyleRunProperties',
    'DocumentFormat.OpenXml.Wordprocessing.StyleTableProperties',
    'DocumentFormat.OpenXml.Wordprocessing.DocDefaults',
    'DocumentFormat.OpenXml.Wordprocessing.RunPropertiesDefault',
    'DocumentFormat.OpenXml.Wordprocessing.RunPropertiesBaseStyle',
    'DocumentFormat.OpenXml.Wordprocessing.ParagraphPropertiesDefault',
    'DocumentFormat.OpenXml.Wordprocessing.ParagraphPropertiesBaseStyle',
    'DocumentFormat.OpenXml.Wordprocessing.Settings',
    'DocumentFormat.OpenXml.Wordprocessing.Footnotes',
    'DocumentFormat.OpenXml.Wordprocessing.Footnote'
)

Write-Host "`n--- numbering ---"
Check @(
    'DocumentFormat.OpenXml.Wordprocessing.Numbering',
    'DocumentFormat.OpenXml.Wordprocessing.AbstractNum',
    'DocumentFormat.OpenXml.Wordprocessing.AbstractNumId',
    'DocumentFormat.OpenXml.Wordprocessing.NumberingInstance',
    'DocumentFormat.OpenXml.Wordprocessing.Level',
    'DocumentFormat.OpenXml.Wordprocessing.LevelText',
    'DocumentFormat.OpenXml.Wordprocessing.LevelJustification',
    'DocumentFormat.OpenXml.Wordprocessing.StartNumberingValue',
    'DocumentFormat.OpenXml.Wordprocessing.NumberingFormat',
    'DocumentFormat.OpenXml.Wordprocessing.MultiLevelType',
    'DocumentFormat.OpenXml.Wordprocessing.LevelOverride',
    'DocumentFormat.OpenXml.Wordprocessing.StartOverrideNumberingValue',
    'DocumentFormat.OpenXml.Wordprocessing.NumberingSymbolRunProperties',
    'DocumentFormat.OpenXml.Wordprocessing.PreviousParagraphProperties'
)

Write-Host "`n--- uiPriority / misc search ---"
$types | Where-Object { $_.Name -match 'Priority|Ui' } | ForEach-Object { Write-Host ("  " + $_.FullName) }

Write-Host "`n--- Math namespace ---"
Check @(
    'DocumentFormat.OpenXml.Math.OfficeMath',
    'DocumentFormat.OpenXml.Math.Paragraph',
    'DocumentFormat.OpenXml.Math.Run',
    'DocumentFormat.OpenXml.Math.RunProperties',
    'DocumentFormat.OpenXml.Math.Text',
    'DocumentFormat.OpenXml.Math.Fraction',
    'DocumentFormat.OpenXml.Math.Numerator',
    'DocumentFormat.OpenXml.Math.Denominator',
    'DocumentFormat.OpenXml.Math.Superscript',
    'DocumentFormat.OpenXml.Math.Subscript',
    'DocumentFormat.OpenXml.Math.SubSuperscript',
    'DocumentFormat.OpenXml.Math.SuperArgument',
    'DocumentFormat.OpenXml.Math.SubArgument',
    'DocumentFormat.OpenXml.Math.Base',
    'DocumentFormat.OpenXml.Math.Radical',
    'DocumentFormat.OpenXml.Math.Degree',
    'DocumentFormat.OpenXml.Math.Nary',
    'DocumentFormat.OpenXml.Math.NaryProperties',
    'DocumentFormat.OpenXml.Math.Delimiter',
    'DocumentFormat.OpenXml.Math.DelimiterProperties',
    'DocumentFormat.OpenXml.Math.BeginChar',
    'DocumentFormat.OpenXml.Math.EndChar',
    'DocumentFormat.OpenXml.Math.Accent',
    'DocumentFormat.OpenXml.Math.AccentProperties',
    'DocumentFormat.OpenXml.Math.Bar',
    'DocumentFormat.OpenXml.Math.BarProperties',
    'DocumentFormat.OpenXml.Math.Matrix',
    'DocumentFormat.OpenXml.Math.MatrixRow',
    'DocumentFormat.OpenXml.Math.EquationArray',
    'DocumentFormat.OpenXml.Math.NormalText',
    'DocumentFormat.OpenXml.Math.Style',
    'DocumentFormat.OpenXml.Math.Limit',
    'DocumentFormat.OpenXml.Math.LimitLower',
    'DocumentFormat.OpenXml.Math.LimitUpper',
    'DocumentFormat.OpenXml.Math.BorderBox',
    'DocumentFormat.OpenXml.Math.Box',
    'DocumentFormat.OpenXml.Math.FractionProperties',
    'DocumentFormat.OpenXml.Math.Type',
    'DocumentFormat.OpenXml.Math.MathFont',
    'DocumentFormat.OpenXml.Math.MathProperties',
    'DocumentFormat.OpenXml.Math.LimitLocation',
    'DocumentFormat.OpenXml.Math.Character',
    'DocumentFormat.OpenXml.Math.HideSubArgument',
    'DocumentFormat.OpenXml.Math.HideSuperArgument',
    'DocumentFormat.OpenXml.Math.StyleValues',
    'DocumentFormat.OpenXml.Math.GroupChar',
    'DocumentFormat.OpenXml.Math.PreSubSuper',
    'DocumentFormat.OpenXml.Math.MatrixProperties',
    'DocumentFormat.OpenXml.Math.Columns',
    'DocumentFormat.OpenXml.Math.Column',
    'DocumentFormat.OpenXml.Math.ControlProperties'
)

Write-Host "`n--- Drawing / pictures ---"
Check @(
    'DocumentFormat.OpenXml.Drawing.Wordprocessing.Inline',
    'DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent',
    'DocumentFormat.OpenXml.Drawing.Wordprocessing.DocProperties',
    'DocumentFormat.OpenXml.Drawing.Wordprocessing.EffectExtent',
    'DocumentFormat.OpenXml.Drawing.Wordprocessing.HorizontalPosition',
    'DocumentFormat.OpenXml.Drawing.Wordprocessing.VerticalPosition',
    'DocumentFormat.OpenXml.Drawing.Wordprocessing.Anchor',
    'DocumentFormat.OpenXml.Drawing.Graphic',
    'DocumentFormat.OpenXml.Drawing.GraphicData',
    'DocumentFormat.OpenXml.Drawing.Blip',
    'DocumentFormat.OpenXml.Drawing.BlipFill',
    'DocumentFormat.OpenXml.Drawing.Stretch',
    'DocumentFormat.OpenXml.Drawing.FillRectangle',
    'DocumentFormat.OpenXml.Drawing.Transform2D',
    'DocumentFormat.OpenXml.Drawing.Offset',
    'DocumentFormat.OpenXml.Drawing.Extents',
    'DocumentFormat.OpenXml.Drawing.PresetGeometry',
    'DocumentFormat.OpenXml.Drawing.AdjustValueList',
    'DocumentFormat.OpenXml.Drawing.Pictures.Picture',
    'DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureProperties',
    'DocumentFormat.OpenXml.Drawing.Pictures.NonVisualDrawingProperties',
    'DocumentFormat.OpenXml.Drawing.Pictures.NonVisualPictureDrawingProperties',
    'DocumentFormat.OpenXml.Drawing.Pictures.BlipFill',
    'DocumentFormat.OpenXml.Drawing.Pictures.ShapeProperties',
    'DocumentFormat.OpenXml.Drawing.Pictures.PictureLocks'
)
