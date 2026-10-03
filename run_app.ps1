$exe = "f:\Application\Aravals Stream\src\AravalsStream.App\bin\Debug\net8.0-windows10.0.19041.0\AravalsStream.App.exe"
$p = Start-Process -FilePath $exe -PassThru
Write-Output "Aravals Stream started with PID $($p.Id)"
$p.WaitForExit()
