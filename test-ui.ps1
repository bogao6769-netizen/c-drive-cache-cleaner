param([string]$OutputDirectory = 'ui-test')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'dist\CDriveCacheCleaner.exe'))
$formType = $assembly.GetType('CDriveCacheCleaner.MainForm', $true)
$form = [Activator]::CreateInstance($formType)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$destination = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
[IO.Directory]::CreateDirectory($destination) | Out-Null
try {
    $form.Show()
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 30
        if ($timer.Elapsed.TotalSeconds -gt 90) { throw 'UI scan timeout' }
    } while ($formType.GetField('_busy', $flags).GetValue($form))
    $grid = $formType.GetField('_grid', $flags).GetValue($form)
    if ($grid.Rows.Count -ne 8) { throw "Unexpected category count: $($grid.Rows.Count)" }
    foreach ($size in @(@(1200,840), @(980,720))) {
        $form.Size = [Drawing.Size]::new($size[0], $size[1])
        [Windows.Forms.Application]::DoEvents()
        foreach ($name in @('_scanButton', '_cleanButton', '_stopButton', '_detailsButton', '_exportButton')) {
            $button = $formType.GetField($name, $flags).GetValue($form)
            if ($button.ClientSize.Width -lt 90 -or $button.ClientSize.Height -lt 25) { throw "Clipped button: $name" }
        }
        if (($grid.Columns | Measure-Object -Property Width -Sum).Sum -gt $grid.ClientSize.Width + 5) { throw 'Main table columns overflow' }
        $side = $formType.GetField('_detailsButton', $flags).GetValue($form).Parent
        if ($grid.Parent.Bounds.IntersectsWith($side.Bounds)) { throw 'Main table overlaps sidebar' }
        $bitmap = [Drawing.Bitmap]::new($form.Width, $form.Height)
        try {
            $form.DrawToBitmap($bitmap, [Drawing.Rectangle]::new(0,0,$bitmap.Width,$bitmap.Height))
            $bitmap.Save((Join-Path $destination "main-$($size[0])x$($size[1]).png"), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $bitmap.Dispose() }
    }
    $capture = @{ Done = $false; Error = $null }
    $dialogTimer = [Windows.Forms.Timer]::new()
    $dialogTimer.Interval = 200
    $dialogTimer.add_Tick({
        $detail = @([Windows.Forms.Application]::OpenForms | Where-Object { $_ -ne $form }) | Select-Object -First 1
        if ($null -eq $detail) { return }
        $dialogTimer.Stop()
        try {
            $bitmap = [Drawing.Bitmap]::new($detail.Width,$detail.Height)
            try {
                $detail.DrawToBitmap($bitmap,[Drawing.Rectangle]::new(0,0,$bitmap.Width,$bitmap.Height))
                $bitmap.Save((Join-Path $destination 'details.png'),[Drawing.Imaging.ImageFormat]::Png)
            } finally { $bitmap.Dispose() }
            $capture.Done = $true
        } catch { $capture.Error = $_.ToString() }
        finally { $detail.Close() }
    })
    try {
        $dialogTimer.Start()
        $formType.GetMethod('ShowDetails',$flags).Invoke($form,@()) | Out-Null
        if (-not $capture.Done -or $capture.Error) { throw "Detail dialog failed: $($capture.Error)" }
    } finally { $dialogTimer.Stop(); $dialogTimer.Dispose() }
    $formType.GetMethod('BeginScan',$flags).Invoke($form,@()) | Out-Null
    $formType.GetMethod('StopOperation',$flags).Invoke($form,@()) | Out-Null
    $timer.Restart()
    while ($formType.GetField('_busy',$flags).GetValue($form)) {
        [Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 30
        if ($timer.Elapsed.TotalSeconds -gt 15) { throw 'Stop scan timeout' }
    }
    if ($formType.GetField('_cleanButton',$flags).GetValue($form).Enabled) { throw 'Stale scan remains cleanable after stopping' }
    Write-Host 'UI smoke test passed; no real cleanup was executed.'
} finally { $form.Close(); $form.Dispose() }
