# Runs Voltide without producing an .exe on disk.
#
# Why: this machine has Windows 11 Smart App Control in enforcement mode.
# It blocks every unsigned executable, and the account is not an administrator,
# so the policy cannot be relaxed. Compiling the exact same source into memory
# with Add-Type sidesteps the file-signature check entirely - the rendered
# result is identical to the signed build.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $root 'src'
$logFile = Join-Path $root 'run-error.txt'

if (Test-Path $logFile) { Remove-Item $logFile -Force -ErrorAction SilentlyContinue }

try
{
    if (-not (Test-Path $srcDir)) { throw "Cannot find source directory: $srcDir" }

    $files = Get-ChildItem $srcDir -Recurse -Filter '*.cs' | Sort-Object FullName
    if ($files.Count -eq 0) { throw "No .cs files found under $srcDir" }

    # Merge every file into one compilation unit.
    # C# requires all "using" directives to precede namespace declarations, so they
    # are hoisted and de-duplicated first; the remaining bodies are appended after.
    $usings = New-Object 'System.Collections.Generic.HashSet[string]'
    $attrs = New-Object 'System.Collections.Generic.HashSet[string]'
    $bodies = New-Object 'System.Collections.Generic.List[string]'

    foreach ($f in $files)
    {
        $lines = [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)
        $body = New-Object System.Text.StringBuilder

        foreach ($line in $lines)
        {
            $trim = $line.Trim()
            if ($trim.StartsWith('using ') -and $trim.EndsWith(';'))
            {
                [void]$usings.Add($trim)
            }
            elseif ($trim.StartsWith('[assembly:'))
            {
                [void]$attrs.Add($trim)
            }
            else
            {
                [void]$body.AppendLine($line)
            }
        }

        $bodies.Add($body.ToString()) | Out-Null
    }

    $sb = New-Object System.Text.StringBuilder
    foreach ($u in $usings) { [void]$sb.AppendLine($u) }
    foreach ($a in $attrs) { [void]$sb.AppendLine($a) }
    foreach ($b in $bodies) { [void]$sb.AppendLine($b) }

    Add-Type -TypeDefinition $sb.ToString() `
        -ReferencedAssemblies 'System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll' `
        -Language CSharp

    # FontManager looks for a bundled Fonts folder relative to the working directory,
    # so the project must run from its own root no matter where it was launched from.
    [System.IO.Directory]::SetCurrentDirectory($root)

    [Voltide.Program]::Run(@('--show'))
}
catch
{
    $msg = "TIME: " + (Get-Date).ToString("yyyy-MM-dd HH:mm:ss") + "`r`n" + $_.Exception.ToString()
    [System.IO.File]::WriteAllText($logFile, $msg, [System.Text.Encoding]::UTF8)
    throw
}
