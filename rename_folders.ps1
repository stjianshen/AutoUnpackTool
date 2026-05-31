$folders = Get-ChildItem -Path "L:\写真\A-240" -Directory | Where-Object { $_.Name -match '^[a-f0-9]{8}_' }

foreach ($folder in $folders) {
    $oldName = $folder.Name
    $newName = $oldName -replace '^[a-f0-9]{8}_', ''
    $oldPath = $folder.FullName
    $newPath = Join-Path $folder.DirectoryName $newName
    
    Write-Host "重命名: $oldName"
    Write-Host "       -> $newName"
    
    try {
        Rename-Item -Path $oldPath -NewName $newName
        Write-Host "成功!" -ForegroundColor Green
    }
    catch {
        Write-Host "失败: $_" -ForegroundColor Red
    }
    Write-Host ""
}
