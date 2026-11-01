$ErrorActionPreference = 'Stop'
$libDir = "$env:USERPROFILE\.nuget\packages\documentformat.openxml\3.5.1\lib\net8.0"
$frameworkDir = "$env:USERPROFILE\.nuget\packages\documentformat.openxml.framework\3.5.1\lib\net8.0"
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
$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $libDir 'DocumentFormat.OpenXml.dll'))
$types = $asm.GetExportedTypes()

Write-Host "--- Math namespace (all public types) ---"
$types | Where-Object { $_.Namespace -eq 'DocumentFormat.OpenXml.Math' } | Sort-Object Name | ForEach-Object { Write-Host ("  " + $_.Name) }

Write-Host "--- PictureLocks 位置 ---"
$types | Where-Object { $_.Name -like '*PictureLocks*' } | ForEach-Object { Write-Host ("  " + $_.FullName) }

Write-Host "--- Math Fraction 子元素 ---"
$t = $types | Where-Object { $_.FullName -eq 'DocumentFormat.OpenXml.Math.Fraction' }
$t.GetProperties('Public,Instance') | ForEach-Object { Write-Host ("  P " + $_.Name + " : " + $_.PropertyType.Name) }

Write-Host "--- Math NaryProperties 子元素 ---"
$t = $types | Where-Object { $_.FullName -eq 'DocumentFormat.OpenXml.Math.NaryProperties' }
$t.GetProperties('Public,Instance') | ForEach-Object { Write-Host ("  P " + $_.Name + " : " + $_.PropertyType.Name) }

Write-Host "--- Math Matrix 子元素 ---"
$t = $types | Where-Object { $_.FullName -eq 'DocumentFormat.OpenXml.Math.Matrix' }
$t.GetProperties('Public,Instance') | ForEach-Object { Write-Host ("  P " + $_.Name + " : " + $_.PropertyType.Name) }

Write-Host "--- AbstractNum 属性 ---"
$t = $types | Where-Object { $_.FullName -eq 'DocumentFormat.OpenXml.Wordprocessing.AbstractNum' }
$t.GetProperties('Public,Instance') | ForEach-Object { Write-Host ("  P " + $_.Name + " : " + $_.PropertyType.Name) }

Write-Host "--- Bold.Val / Italic.Val 类型 ---"
foreach ($n in @('Bold','Italic','Strike','Underline','Highlight','FontSize','Color')) {
    $t = $types | Where-Object { $_.FullName -eq "DocumentFormat.OpenXml.Wordprocessing.$n" }
    if ($t) {
        $vals = $t.GetProperties('Public,Instance') | Where-Object { $_.Name -eq 'Val' }
        if ($vals) { Write-Host ("  " + $n + ".Val : " + $vals.PropertyType.FullName) } else { Write-Host ("  " + $n + " : (no Val)") }
    }
}

Write-Host "--- Footnote 属性 ---"
$t = $types | Where-Object { $_.FullName -eq 'DocumentFormat.OpenXml.Wordprocessing.Footnote' }
$t.GetProperties('Public,Instance') | ForEach-Object { Write-Host ("  P " + $_.Name + " : " + $_.PropertyType.Name) }

Write-Host "--- NumberingInstance 属性 ---"
$t = $types | Where-Object { $_.FullName -eq 'DocumentFormat.OpenXml.Wordprocessing.NumberingInstance' }
$t.GetProperties('Public,Instance') | ForEach-Object { Write-Host ("  P " + $_.Name + " : " + $_.PropertyType.Name) }

Write-Host "--- Level 属性 ---"
$t = $types | Where-Object { $_.FullName -eq 'DocumentFormat.OpenXml.Wordprocessing.Level' }
$t.GetProperties('Public,Instance') | ForEach-Object { Write-Host ("  P " + $_.Name + " : " + $_.PropertyType.Name) }

Write-Host "--- Style 属性 ---"
$t = $types | Where-Object { $_.FullName -eq 'DocumentFormat.OpenXml.Wordprocessing.Style' }
$t.GetProperties('Public,Instance') | ForEach-Object { Write-Host ("  P " + $_.Name + " : " + $_.PropertyType.Name) }

Write-Host "--- Wordprocessing.Table 属性 ---"
$t = $types | Where-Object { $_.FullName -eq 'DocumentFormat.OpenXml.Wordprocessing.Table' }
$t.GetProperties('Public,Instance') | ForEach-Object { Write-Host ("  P " + $_.Name + " : " + $_.PropertyType.Name) }
