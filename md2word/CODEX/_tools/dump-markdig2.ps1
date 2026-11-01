$ErrorActionPreference = 'Stop'
$asm = [System.Reflection.Assembly]::LoadFrom('C:\Users\AZURE\.nuget\packages\markdig\1.4.0\lib\net8.0\Markdig.dll')
$types = $asm.GetExportedTypes()

Write-Host "--- Diagrams namespace ---"
$types | Where-Object { $_.Namespace -eq 'Markdig.Extensions.Diagrams' } | ForEach-Object {
    Write-Host ("== " + $_.FullName + " (base " + $_.BaseType.Name + ")")
    $_.GetFields('Public,Instance,FlattenHierarchy') | ForEach-Object { Write-Host ("   F " + $_.Name + " : " + $_.FieldType.Name) }
    $_.GetProperties('Public,Instance,FlattenHierarchy') | ForEach-Object { Write-Host ("   P " + $_.Name + " : " + $_.PropertyType.Name) }
}

Write-Host "--- MarkdownObjectExtensions ---"
$types | Where-Object { $_.Name -eq 'MarkdownObjectExtensions' } | ForEach-Object {
    Write-Host $_.FullName
    $_.GetMethods('Public,Static') | ForEach-Object {
        Write-Host ("   " + $_.Name + "(" + (($_.GetParameters() | ForEach-Object { $_.ParameterType.Name }) -join ',') + ") -> " + $_.ReturnType.Name)
    }
}

Write-Host "--- Markdig.Extensions.Alerts ---"
$types | Where-Object { $_.Namespace -eq 'Markdig.Extensions.Alerts' } | ForEach-Object { Write-Host $_.FullName }

Write-Host "--- StringLineGroup / StringLine ---"
$types | Where-Object { $_.Name -in @('StringLineGroup','StringLine') } | ForEach-Object {
    Write-Host ("== " + $_.FullName)
    $_.GetFields('Public,Instance,FlattenHierarchy') | ForEach-Object { Write-Host ("   F " + $_.Name + " : " + $_.FieldType.Name) }
    $_.GetProperties('Public,Instance,FlattenHierarchy') | ForEach-Object { Write-Host ("   P " + $_.Name + " : " + $_.PropertyType.Name) }
}
