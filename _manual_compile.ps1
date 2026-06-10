$base='obj\\x64\\Debug\\net8.0-windows10.0.19041.0\\win-x64'
$input = Get-Content "$base\\input.json" -Raw | ConvertFrom-Json
$refs = $input.ReferenceAssemblies | ForEach-Object { $_.FullPath } | Sort-Object -Unique
$src = Get-ChildItem -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\bin\\' } | ForEach-Object { $_.FullName }
$g = Get-ChildItem "$base" -Filter '*.g.i.cs' | ForEach-Object { $_.FullName }
$tmpG = Join-Path $base 'EditorPage.g.temp.cs'
$backup = 'obj\\Debug\\net8.0-windows10.0.19041.0\\EditorPage.g.cs.backup'
Copy-Item $backup $tmpG -Force
$all = @('/nologo','/target:library','/langversion:latest','/nullable:enable','/deterministic')
foreach($r in $refs){ if(Test-Path $r){ $all += ('/r:"' + $r + '"') } }
foreach($s in ($src + $g + $tmpG)){ if(Test-Path $s){ $all += ('"' + $s + '"') } }
Set-Content -Path compile_editor.rsp -Value $all -Encoding UTF8
$sdk=(dotnet --version).Trim()
$csc="C:\\Program Files\\dotnet\\sdk\\$sdk\\Roslyn\\bincore\\csc.dll"
dotnet $csc "@compile_editor.rsp" > compile_editor_stdout.txt 2> compile_editor_stderr.txt
$code=$LASTEXITCODE
Remove-Item $tmpG -Force
Write-Host "ManualCompileExit=$code"
