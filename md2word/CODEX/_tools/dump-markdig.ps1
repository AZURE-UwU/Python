$ErrorActionPreference = 'Stop'

$dll = Get-ChildItem -Path "$env:USERPROFILE\.nuget\packages\markdig\1.4.0\lib" -Recurse -Filter Markdig.dll |
    Where-Object { $_.FullName -match 'net8\.0' } | Select-Object -First 1
if (-not $dll) { $dll = Get-ChildItem -Path "$env:USERPROFILE\.nuget\packages\markdig\1.4.0\lib" -Recurse -Filter Markdig.dll | Select-Object -First 1 }

Write-Host "ASSEMBLY: $($dll.FullName)"
$asm = [System.Reflection.Assembly]::LoadFrom($dll.FullName)

try { $types = $asm.GetExportedTypes() } catch { $types = $_.Exception.Types | Where-Object { $_ -ne $null } }

function Dump-Type([Type]$t) {
    $base = if ($t.BaseType) { $t.BaseType.Name } else { '-' }
    Write-Host "== $($t.FullName)  (base: $base)"
    foreach ($f in $t.GetFields([System.Reflection.BindingFlags]'Public,Instance')) {
        $ft = $f.FieldType
        $ftn = if ($ft.IsGenericType) { "$($ft.Name)<" + (($ft.GetGenericArguments() | ForEach-Object { $_.Name }) -join ',') + ">" } else { $ft.Name }
        Write-Host "    F $($f.Name) : $ftn"
    }
    foreach ($p in $t.GetProperties([System.Reflection.BindingFlags]'Public,Instance,DeclaredOnly')) {
        $pt = $p.PropertyType
        $ptn = if ($pt.IsGenericType) { "$($pt.Name)<" + (($pt.GetGenericArguments() | ForEach-Object { $_.Name }) -join ',') + ">" } else { $pt.Name }
        Write-Host "     .$($p.Name) : $ptn"
    }
}

Write-Host "`n########## INHERITED MEMBERS OF KEY TYPES ##########"
foreach ($n in @(
    'Markdig.Syntax.LeafBlock','Markdig.Syntax.Block','Markdig.Syntax.MarkdownObject',
    'Markdig.Syntax.Inlines.LiteralInline','Markdig.Extensions.Mathematics.MathInline',
    'Markdig.Extensions.Mathematics.MathBlock','Markdig.Syntax.Inlines.EmphasisInline',
    'Markdig.Syntax.HeadingBlock','Markdig.Syntax.ParagraphBlock',
    'Markdig.Extensions.Tables.Table','Markdig.Extensions.Tables.TableColumnDefinition',
    'Markdig.Extensions.Footnotes.Footnote','Markdig.Syntax.ListBlock','Markdig.Syntax.ListItemBlock',
    'Markdig.Syntax.Inlines.LinkInline','Markdig.Extensions.DefinitionLists.DefinitionTerm'
)) {
    $t = $types | Where-Object { $_.FullName -eq $n }
    if (-not $t) { Write-Host "== (missing) $n"; continue }
    Write-Host "== ALL MEMBERS: $n"
    foreach ($f in $t.GetFields([System.Reflection.BindingFlags]'Public,Instance,FlattenHierarchy')) {
        $ft = $f.FieldType
        $ftn = if ($ft.IsGenericType) { "$($ft.Name)<" + (($ft.GetGenericArguments() | ForEach-Object { $_.Name }) -join ',') + ">" } else { $ft.Name }
        Write-Host "    F $($f.Name) : $ftn   [decl: $($f.DeclaringType.Name)]"
    }
    foreach ($p in $t.GetProperties([System.Reflection.BindingFlags]'Public,Instance,FlattenHierarchy')) {
        $pt = $p.PropertyType
        $ptn = if ($pt.IsGenericType) { "$($pt.Name)<" + (($pt.GetGenericArguments() | ForEach-Object { $_.Name }) -join ',') + ">" } else { $pt.Name }
        Write-Host "    P $($p.Name) : $ptn   [decl: $($p.DeclaringType.Name)]"
    }
}

Write-Host "`n########## MarkdownExtensions (Use* / Get* / GetAttributes) ##########"
$mx = $types | Where-Object { $_.FullName -eq 'Markdig.MarkdownExtensions' }
$mx.GetMethods([System.Reflection.BindingFlags]'Public,Static') | ForEach-Object {
    "$($_.Name)(" + (($_.GetParameters() | ForEach-Object { "$($_.ParameterType.Name)" }) -join ', ') + ")"
} | Sort-Object -Unique

Write-Host "`n########## BLOCK TYPES ##########"
$blockBase = $types | Where-Object { $_.FullName -eq 'Markdig.Syntax.Block' }
$blockTypes = $types | Where-Object { $_ -ne $blockBase -and $blockBase.IsAssignableFrom($_) -and -not $_.IsAbstract }
foreach ($t in ($blockTypes | Sort-Object FullName)) { Dump-Type $t }

Write-Host "`n########## INLINE TYPES ##########"
$inlineBase = $types | Where-Object { $_.FullName -eq 'Markdig.Syntax.Inlines.Inline' }
$inlineTypes = $types | Where-Object { $_ -ne $inlineBase -and $inlineBase.IsAssignableFrom($_) -and -not $_.IsAbstract }
foreach ($t in ($inlineTypes | Sort-Object FullName)) { Dump-Type $t }

Write-Host "`n########## PIPELINE BUILDER Use* METHODS ##########"
$pb = $types | Where-Object { $_.FullName -eq 'Markdig.MarkdownPipelineBuilder' }
$pb.GetMethods() | Where-Object { $_.Name -like 'Use*' } | ForEach-Object { $_.Name } | Sort-Object -Unique

Write-Host "`n########## STATIC Markdown / MarkdownObject EXTENSIONS ##########"
$ext = $types | Where-Object { $_.FullName -eq 'Markdig.Markdown' }
$ext.GetMethods([System.Reflection.BindingFlags]'Public,Static') | ForEach-Object {
    "$($_.Name)(" + (($_.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', ') + ")"
} | Sort-Object -Unique

Write-Host "`n########## StringSlice / helpers ##########"
foreach ($n in @('Markdig.Helpers.StringSlice','Markdig.Syntax.MarkdownObject','Markdig.Syntax.ContainerBlock','Markdig.Syntax.LeafBlock','Markdig.Syntax.Inlines.ContainerInline','Markdig.Syntax.Inlines.LeafInline','Markdig.Syntax.MarkdownDocument','Markdig.MarkdownPipeline')) {
    $t = $types | Where-Object { $_.FullName -eq $n }
    if ($t) { Dump-Type $t } else { Write-Host "== (missing) $n" }
}
