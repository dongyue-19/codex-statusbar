<#
.SYNOPSIS
  Focused read-only UIA inspection of the Codex composer toolbar row.

.DESCRIPTION
  Locates the composer subtree, prints its hierarchy, and dumps every UIA property
  of the Context-usage indicator and the Model selector, plus their ancestor chains
  and sibling geometry. Used to decide which identifier is stable enough to anchor
  the status strip to. Writes nothing and clicks nothing.
#>
[CmdletBinding()]
param(
    [int]$Depth = 8,
    [string]$Json,
    [int]$WindowHandle = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CUiaNat {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    public static bool MakeDpiAware() { return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
}
'@
[void][CUiaNat]::MakeDpiAware()

function SafeInt($v) { if ($null -eq $v) { return 0 }; $d = [double]$v; if ([double]::IsNaN($d) -or [double]::IsInfinity($d)) { return -1 }; if ($d -gt 2e9) { return 2000000000 }; if ($d -lt -2e9) { return -2000000000 }; return [int]$d }
function Rect($el) { $r = $el.Current.BoundingRectangle; "($(SafeInt $r.X),$(SafeInt $r.Y)) $(SafeInt $r.Width)x$(SafeInt $r.Height)" }
function Tag($el) {
    try { $c = $el.Current } catch { return '<stale>' }
    $t = $c.ControlType.ProgrammaticName -replace '^ControlType\.',''
    $id = if ($c.AutomationId) { "#$($c.AutomationId)" } else { '' }
    $n = if ($c.Name) { " '$((($c.Name -replace '\s+',' ')).Substring(0,[Math]::Min(30,(($c.Name -replace '\s+',' ')).Length)))'" } else { '' }
    "$t$id$n"
}

$hwnd = if ($WindowHandle -ne 0) { [IntPtr]$WindowHandle } else {
    $best = [IntPtr]::Zero; $bestArea = 0
    foreach ($p in Get-Process ChatGPT -ErrorAction SilentlyContinue) {
        $h = $p.MainWindowHandle
        if ($h -eq [IntPtr]::Zero) { continue }
        $r = New-Object CUiaNat+RECT
        [void][CUiaNat]::DwmGetWindowAttribute($h, 9, [ref]$r, 16)
        $a = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
        if ($a -gt $bestArea) { $bestArea = $a; $best = $h }
    }
    $best
}

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
Write-Host "root = $(Tag $root) $(Rect $root)"

$ctrl = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$raw  = [System.Windows.Automation.TreeWalker]::RawViewWalker

function Find-All($el, $pred, $depth, $maxDepth) {
    $out = New-Object System.Collections.Generic.List[object]
    if ($depth -gt $maxDepth) { return $out }
    $c = $null
    try { $c = $ctrl.GetFirstChild($el) } catch { return $out }
    while ($null -ne $c) {
        $hit = $false
        try { $hit = & $pred $c } catch {}
        if ($hit) { $out.Add($c) }
        foreach ($d in (Find-All $c $pred ($depth+1) $maxDepth)) { $out.Add($d) }
        try { $c = $ctrl.GetNextSibling($c) } catch { break }
    }
    $out
}

# ---- 1. the Context usage indicator -------------------------------------------------
Write-Host "`n=== Context usage indicator candidates (Name starts with 上下文用量) ==="
$ctxPred = { param($e) $e.Current.Name -like '上下文用量*' }
$ctxs = Find-All $root $ctxPred 0 30
foreach ($e in $ctxs) { Write-Host "  $(Tag $e)  $(Rect $e)" }

$ctx = $ctxs | Select-Object -First 1

# ---- 2. the Model selector ----------------------------------------------------------
Write-Host "`n=== Model selector candidates (Button right of the context indicator, same row) ==="
$models = New-Object System.Collections.Generic.List[object]
if ($ctx) {
    $cr = $ctx.Current.BoundingRectangle
    $pred = { param($e)
        $c = $e.Current
        $c.ControlType.ProgrammaticName -eq 'ControlType.Button' -and
        [Math]::Abs(($c.BoundingRectangle.Y + $c.BoundingRectangle.Height/2) - ($cr.Y + $cr.Height/2)) -lt 20 -and
        $c.BoundingRectangle.X -ge ($cr.X - 4)
    }
    foreach ($e in (Find-All $root $pred 0 30)) { $models.Add($e) }
}
foreach ($e in $models) { Write-Host "  $(Tag $e)  $(Rect $e)" }
$model = $models | Sort-Object { $_.Current.BoundingRectangle.X } | Select-Object -First 1

# ---- 3. full composer subtree -------------------------------------------------------
Write-Host "`n=== Composer subtree (ancestors of the Context indicator) ==="
$chain = New-Object System.Collections.Generic.List[object]
if ($ctx) {
    $cur = $ctx
    while ($null -ne $cur) {
        $chain.Insert(0, $cur)
        try { $cur = $ctrl.GetParent($cur) } catch { break }
        if ($cur -and $cur.Current.ControlType.ProgrammaticName -eq 'ControlType.Window') { break }
    }
}
foreach ($a in $chain) { Write-Host "  $(Tag $a)  $(Rect $a)" }

$composer = $null
if ($chain.Count -ge 2) { $composer = $chain[$chain.Count - 2] }   # parent of the Context indicator's parent
Write-Host "`nchosen composer subtree root: $(if ($composer) { Tag $composer } else { '<none>' })"

if ($composer) {
    Write-Host "`n=== Composer descendants (with sibling-order paths) ==="
    function Dump($el, $d, $path) {
        if ($d -gt $Depth) { return }
        $c = $null
        try { $c = $ctrl.GetFirstChild($el) } catch { return }
        $i = 0
        while ($null -ne $c) {
            $r = $c.Current.BoundingRectangle
            $row = [pscustomobject]@{
                Depth = $d; Idx = $i; Path = "$path/$i"
                ControlType = ($c.Current.ControlType.ProgrammaticName -replace '^ControlType\.','')
                AutomationId = $c.Current.AutomationId
                ClassName = $c.Current.ClassName
                Name = $c.Current.Name
                X = (SafeInt $r.X); Y = (SafeInt $r.Y); W = (SafeInt $r.Width); H = (SafeInt $r.Height)
            }
            $script:dump += $row
            Dump $c ($d+1) "$path/$i"
            try { $c = $ctrl.GetNextSibling($c) } catch { break }
            $i++
        }
    }
    $script:dump = New-Object System.Collections.Generic.List[object]
    Dump $composer 0 ''
    $script:dump | Format-Table -AutoSize -Wrap | Out-String -Width 320 | Write-Host
}

# ---- 4. every UIA property of the two reference candidates --------------------------
Write-Host "`n=== UIA properties ==="
function DumpProps($label, $el) {
    if (-not $el) { Write-Host "$label : <not found>"; return }
    $c = $el.Current
    Write-Host "-- $label --"
    foreach ($p in @('Name','AutomationId','ClassName','ControlType','LocalizedControlType','AcceleratorKey',
                     'AccessKey','HelpText','ItemStatus','ItemType','FrameworkId','ProcessId','IsContentElement',
                     'IsControlElement','IsEnabled','IsOffscreen','IsPassword','IsRequiredForForm','NativeWindowHandle')) {
        $v = try { $c.$p } catch { '<n/a>' }
        Write-Host ("   {0,-22} = {1}" -f $p, $v)
    }
    $r = $c.BoundingRectangle
    Write-Host ("   {0,-22} = ({1},{2}) {3}x{4}" -f 'BoundingRectangle', [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
    $pats = try { ($el.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName -replace '^Pattern\.','' }) -join ',' } catch { '<n/a>' }
    Write-Host ("   {0,-22} = {1}" -f 'SupportedPatterns', $pats)
}
DumpProps 'Context indicator' $ctx
DumpProps 'Model selector' $model

# ---- 5. RawView check: does RawView expose more siblings? ---------------------------
if ($ctx) {
    Write-Host "`n=== RawView siblings of the Context indicator ==="
    try {
        $p = $raw.GetParent($ctx)
        $s = $raw.GetFirstChild($p)
        while ($null -ne $s) {
            Write-Host ("   {0}  {1}" -f (Tag $s), (Rect $s))
            $s = $raw.GetNextSibling($s)
        }
    } catch { Write-Host "   raw walk failed: $($_.Exception.Message)" }
}

if ($Json) {
    $payload = [pscustomobject]@{
        Window = ('0x{0:X}' -f $hwnd.ToInt64())
        Composer = if ($composer) { [pscustomobject]@{ Name = $composer.Current.Name; AutomationId = $composer.Current.AutomationId; Rect = (Rect $composer) } } else { $null }
        Context = if ($ctx) { [pscustomobject]@{ Name = $ctx.Current.Name; AutomationId = $ctx.Current.AutomationId; ClassName = $ctx.Current.ClassName; ControlType = ($ctx.Current.ControlType.ProgrammaticName -replace '^ControlType\.',''); Rect = (Rect $ctx) } } else { $null }
        Model = if ($model) { [pscustomobject]@{ Name = $model.Current.Name; AutomationId = $model.Current.AutomationId; ClassName = $model.Current.ClassName; Rect = (Rect $model) } } else { $null }
        ComposerTree = $script:dump
    }
    $payload | ConvertTo-Json -Depth 6 | Set-Content -Path $Json -Encoding UTF8
    Write-Host "wrote $Json"
}