<#
.SYNOPSIS
    Tags what's on origin/main as the next release and pushes the tag, which starts release.yml.

.DESCRIPTION
    Finds the latest vX.Y.Z tag, works out the next version (a patch release unless told otherwise), lists what's
    been merged since, checks CI on that commit, and asks before tagging and pushing anything.

.PARAMETER Bump
    Which part of the version to raise: Patch (the default), Minor or Major.

.PARAMETER Version
    The exact version to release (e.g. 0.2.0), instead of working it out.

.EXAMPLE
    ./Git/Release.ps1
.EXAMPLE
    ./Git/Release.ps1 -Bump Minor
#>
[CmdletBinding()]
param(
    [ValidateSet('Patch', 'Minor', 'Major')]
    [string] $Bump = 'Patch',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Git {
    $output = & git @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed." }
    $output
}

Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    Write-Host 'Fetching origin...'
    Invoke-Git fetch --quiet origin main --tags
    $commit = Invoke-Git rev-parse origin/main

    # Releases are vX.Y.Z; anything else (trial or pre-release tags) is ignored.
    $last = Invoke-Git tag --list 'v*' --sort=-v:refname | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } | Select-Object -First 1
    if ($last) {
        if ((Invoke-Git rev-list -n 1 $last) -eq $commit) {
            Write-Host "origin/main is already released as $last. Nothing to do." -ForegroundColor Yellow
            return
        }
        $current = [version]$last.Substring(1)
    }
    else {
        $current = [version]'0.0.0'
    }

    if (-not $Version) {
        $Version = switch ($Bump) {
            'Major' { "$($current.Major + 1).0.0" }
            'Minor' { "$($current.Major).$($current.Minor + 1).0" }
            'Patch' { "$($current.Major).$($current.Minor).$($current.Build + 1)" }
        }
    }
    if ([version]$Version -le $current) { throw "v$Version isn't after the last release, $last." }
    $tag = "v$Version"
    if (Invoke-Git tag --list $tag) { throw "$tag already exists." }

    # What's been merged since: each PR's title (a merge commit's body), or the commit's subject.
    $range = if ($last) { "$last..$commit" } else { $commit }
    $changes = (Invoke-Git log --first-parent --format='%s%x1f%b%x1e' $range) -join "`n" -split "\x1e" |
        Where-Object { $_.Trim() } |
        ForEach-Object {
            $subject, $body = $_.Trim() -split "\x1f", 2
            if ($subject -match '^Merge pull request (#\d+)' -and $body -and $body.Trim()) {
                "$($Matches[1]) $(($body.Trim() -split "`n")[0])"
            }
            else { $subject }
        }

    Write-Host ''
    Write-Host "Release $tag  (last: $(if ($last) { $last } else { 'none' }))" -ForegroundColor Cyan
    Write-Host "Commit  $($commit.Substring(0, 7))  origin/main"
    Write-Host ''
    Write-Host 'Changes since the last release:'
    $changes | ForEach-Object { Write-Host "  - $_" }
    Write-Host ''

    # CI on that commit, if the GitHub CLI is there to ask.
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $runs = & gh run list --workflow ci.yml --commit $commit --json status,conclusion,url 2>$null | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $runs) {
            Write-Host "CI: no run found for this commit." -ForegroundColor Yellow
        }
        else {
            $run = $runs[0]
            if ($run.status -ne 'completed') {
                Write-Host "CI: still running ($($run.url))." -ForegroundColor Yellow
            }
            elseif ($run.conclusion -ne 'success') {
                Write-Host "CI: $($run.conclusion) ($($run.url))." -ForegroundColor Red
            }
            else {
                Write-Host 'CI: passed.' -ForegroundColor Green
            }
        }
    }
    else {
        Write-Host 'CI: not checked (the GitHub CLI, gh, isn''t installed).' -ForegroundColor Yellow
    }
    Write-Host ''

    $answer = Read-Host "Tag $tag and push it to origin? This starts the release build and publishes it. [y/N]"
    if ($answer -notmatch '^(y|yes)$') {
        Write-Host 'Nothing tagged.'
        return
    }

    Invoke-Git tag --annotate $tag $commit --message "PhotoTag $Version"
    & git push origin $tag
    if ($LASTEXITCODE -ne 0) {
        Invoke-Git tag --delete $tag | Out-Null
        throw "Couldn't push $tag; the local tag was removed."
    }

    $remote = (Invoke-Git remote get-url origin) -replace '\.git$', '' -replace '^git@github\.com:', 'https://github.com/'
    Write-Host ''
    Write-Host "Pushed $tag. The release build: $remote/actions/workflows/release.yml" -ForegroundColor Green
}
finally {
    Pop-Location
}
