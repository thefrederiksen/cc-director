<#
.SYNOPSIS
    Finds all cc_ references in C:\ReposFred (file names, directory names, and file contents)
.DESCRIPTION
    Outputs results to a CSV file with columns: Type, Path, Line, Match
#>

param(
    [string]$SearchPath = "C:\ReposFred",
    [string]$OutputFile = "cc_underscore_findings.csv"
)

$results = [System.Collections.ArrayList]::new()

# Text file extensions to search inside
$textExtensions = @(
    ".cs", ".csproj", ".sln", ".xaml", ".xml", ".json", ".yaml", ".yml",
    ".md", ".txt", ".ps1", ".bat", ".cmd", ".sh", ".py", ".js", ".ts",
    ".html", ".css", ".config", ".props", ".targets", ".editorconfig",
    ".gitignore", ".gitattributes", ".env", ".ini", ".toml", ".razor"
)

# Directories to skip
$skipDirs = @("node_modules", ".git", "bin", "obj", ".vs", "__pycache__", "venv", ".venv", "packages")

Write-Host "Searching for 'cc_' in $SearchPath..." -ForegroundColor Cyan
Write-Host ""

# Get all files and directories, excluding skip dirs
$items = Get-ChildItem -Path $SearchPath -Recurse -Force -ErrorAction SilentlyContinue | Where-Object {
    $dominated = $false
    foreach ($skip in $skipDirs) {
        if ($_.FullName -match "\\$skip\\") {
            $dominated = $true
            break
        }
    }
    -not $dominated
}

Write-Host "Found $($items.Count) items to scan" -ForegroundColor Gray

# 1. Find directories with cc_ in name
Write-Host "Checking directory names..." -ForegroundColor Yellow
$dirs = $items | Where-Object { $_.PSIsContainer -and $_.Name -match "cc_" }
foreach ($dir in $dirs) {
    [void]$results.Add([PSCustomObject]@{
        Type = "Directory"
        Path = $dir.FullName
        Line = ""
        Match = $dir.Name
    })
}
Write-Host "  Found $($dirs.Count) directories" -ForegroundColor Gray

# 2. Find files with cc_ in name
Write-Host "Checking file names..." -ForegroundColor Yellow
$files = $items | Where-Object { -not $_.PSIsContainer -and $_.Name -match "cc_" }
foreach ($file in $files) {
    [void]$results.Add([PSCustomObject]@{
        Type = "FileName"
        Path = $file.FullName
        Line = ""
        Match = $file.Name
    })
}
Write-Host "  Found $($files.Count) files" -ForegroundColor Gray

# 3. Search inside text files for cc_
Write-Host "Searching file contents..." -ForegroundColor Yellow
$textFiles = $items | Where-Object {
    -not $_.PSIsContainer -and
    ($textExtensions -contains $_.Extension.ToLower() -or $_.Extension -eq "")
}

$fileCount = 0
$contentMatches = 0
foreach ($file in $textFiles) {
    $fileCount++
    if ($fileCount % 500 -eq 0) {
        Write-Host "  Scanned $fileCount / $($textFiles.Count) files..." -ForegroundColor Gray
    }

    try {
        $lineNum = 0
        foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
            $lineNum++
            if ($line -match "cc_") {
                $contentMatches++
                # Extract the specific match context
                $matchContext = $line.Trim()
                if ($matchContext.Length > 200) {
                    $matchContext = $matchContext.Substring(0, 200) + "..."
                }
                [void]$results.Add([PSCustomObject]@{
                    Type = "Content"
                    Path = $file.FullName
                    Line = $lineNum
                    Match = $matchContext
                })
            }
        }
    }
    catch {
        # Skip files that can't be read
    }
}
Write-Host "  Found $contentMatches content matches in $fileCount files" -ForegroundColor Gray

# Export to CSV
$outputPath = Join-Path (Get-Location) $OutputFile
$results | Export-Csv -Path $outputPath -NoTypeInformation -Encoding UTF8

Write-Host ""
Write-Host "Results saved to: $outputPath" -ForegroundColor Green
Write-Host "Total findings: $($results.Count)" -ForegroundColor Green
Write-Host ""
Write-Host "Summary:" -ForegroundColor Cyan
Write-Host "  Directories: $($dirs.Count)"
Write-Host "  File names:  $($files.Count)"
Write-Host "  Content:     $contentMatches"
