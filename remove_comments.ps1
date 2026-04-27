$excludeDirs = @('.git', '.vs', '.vscode', 'bin', 'obj', 'artifacts', 'installers', 'release', '.agent', '.gemini')
$includeExts = @('.cs', '.xaml', '.xml', '.csproj', '.iss', '.md', '.ps1', '.js', '.manifest')

function Remove-Comments {
    param (
        [string]$Content,
        [string]$Ext
    )

    if ($Ext -in @('.cs', '.js', '.ps1', '.manifest')) {
        # Regex for strings (group 1) or comments (group 2)
        # Note: Powershell regex needs to handle single quotes properly in the pattern string
        $pattern = '("(?:\\.|[^"\\])*"|''(?:\\.|[^''])*'')|(/\*[\s\S]*?\*/|//.*)'
        return [regex]::Replace($Content, $pattern, {
            param($match)
            if ($match.Groups[1].Success) {
                return $match.Groups[1].Value # Keep string
            }
            return "" # Remove comment
        })
    }
    elseif ($Ext -in @('.xaml', '.xml', '.csproj', '.md')) {
        return $Content -replace '(?s)<!--.*?-->', ''
    }
    elseif ($Ext -eq '.iss') {
        $pattern = '("(?:\\.|[^"\\])*")|(;.*|//.*)'
        return [regex]::Replace($Content, $pattern, {
            param($match)
            if ($match.Groups[1].Success) {
                return $match.Groups[1].Value
            }
            return ""
        })
    }
    return $Content
}

$rootPath = Get-Location
Write-Host "Starting comment removal in $rootPath..."

Get-ChildItem -Path $rootPath -Recurse -File | ForEach-Object {
    $file = $_
    $skip = $false
    foreach ($dir in $excludeDirs) {
        if ($file.FullName -like "*\$dir\*") {
            $skip = $true
            break
        }
    }
    
    if (-not $skip -and $file.Extension -in $includeExts) {
        try {
            $content = [System.IO.File]::ReadAllText($file.FullName)
            $newContent = Remove-Comments -Content $content -Ext $file.Extension
            
            if ($content -ne $newContent) {
                [System.IO.File]::WriteAllText($file.FullName, $newContent)
                Write-Host "Processed: $($file.FullName)"
            }
        }
        catch {
            Write-Host "Error processing $($file.FullName): $($_.Exception.Message)"
        }
    }
}

Write-Host "Finished!"
