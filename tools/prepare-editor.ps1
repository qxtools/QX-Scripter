#requires -Version 7.3
param()

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$revision = ([xml][IO.File]::ReadAllText((Join-Path $root 'Editor.props'))).Project.PropertyGroup.RoslynPadRevision
if ($revision -notmatch '^[a-f0-9]{40}$') { throw 'RoslynPadRevision must be a full Git commit.' }
$source = Join-Path $root "artifacts/editor/$revision"
$patch = Join-Path $root 'vendor/RoslynPad.Editor.Avalonia/keyboard.patch'
if (!(Test-Path -LiteralPath (Join-Path $source '.git'))) {
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    & git -C $source init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the editor checkout.' }
}
& git -C $source sparse-checkout set src/RoslynPad.Editor.Avalonia src/RoslynPad.Editor.Windows/Shared
if ($LASTEXITCODE -ne 0) { throw 'Could not configure the editor checkout.' }
$actual = & git -C $source rev-parse --verify HEAD 2>$null
if ($LASTEXITCODE -ne 0 -or $actual -ne $revision) {
    & git -C $source fetch --quiet --depth 1 --filter=blob:none https://github.com/roslynpad/roslynpad.git $revision
    if ($LASTEXITCODE -ne 0) { throw 'Could not download the pinned editor source.' }
    & git -C $source checkout --quiet --detach FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not check out the pinned editor source.' }
}
$actual = & git -C $source rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $actual -ne $revision) { throw 'The editor checkout does not match RoslynPadRevision.' }
$extra = & git -C $source ls-files --others --exclude-standard -- src/RoslynPad.Editor.Avalonia src/RoslynPad.Editor.Windows/Shared
if ($LASTEXITCODE -ne 0 -or $extra) { throw 'The editor checkout contains unexpected source files.' }
$expected = [IO.File]::ReadAllText($patch).Replace("`r`n", "`n").Trim()
$diff_arguments = @('diff', '--no-ext-diff', '--no-textconv', '--no-color', '--no-renames', '--abbrev=7', '--src-prefix=a/', '--dst-prefix=b/', '--diff-algorithm=myers', 'HEAD')
$difference = (& git -C $source @diff_arguments | Out-String).Replace("`r`n", "`n").Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not verify the editor source.' }
if (!$difference) {
    & git -C $source apply --whitespace=error $patch
    if ($LASTEXITCODE -ne 0) { throw 'Could not apply the editor correction.' }
    $difference = (& git -C $source @diff_arguments | Out-String).Replace("`r`n", "`n").Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not verify the editor correction.' }
}
if ($difference -ne $expected) { throw 'The editor source contains unexpected changes.' }
[IO.File]::WriteAllText((Join-Path $source '.qx-prepared'), $revision)
Write-Output $source
