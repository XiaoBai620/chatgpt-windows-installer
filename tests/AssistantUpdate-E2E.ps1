# Run in a non-elevated Windows x64 PowerShell. Uses ONLY local project builds.
# The old fixture substitutes the HTTP metadata/body; both processes, replacement,
# rollback, startup acknowledgement and the restarted WPF home are real.
$ErrorActionPreference='Stop'
if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Use a non-elevated PowerShell.' }
$project=Join-Path $PSScriptRoot '../desktop/PackagePicker.csproj'
function Publish([string]$out,[string]$version,[string]$define) {
    $options=@('publish',$project,'-c','Release','-r','win-x64','--self-contained','false',"-p:Version=$version",'-o',$out)
    if ($define) { $options+="-p:DefineConstants=$define" }
    & dotnet @options
    if ($LASTEXITCODE) {throw 'Fixture build failed.'}
}
foreach ($rollback in @($false,$true)) {
    $root=Join-Path ([IO.Path]::GetTempPath()) ('PickerE2E-'+[guid]::NewGuid().ToString('N'))
    $exe=Join-Path $root 'ChatGPT-PackagePicker.exe'; $fixture=Join-Path $root 'fixture'; $new=Join-Path $fixture 'ChatGPT-PackagePicker.exe'
    try {
        Publish $root '1.0.3-rc1' 'UPDATE_FIXTURE'
        $define=if($rollback){'UPDATE_BROKEN_FIXTURE'}else{''}; Publish $fixture '1.0.4' $define
        $oldHash=(Get-FileHash -LiteralPath $exe).Hash;$newHash=(Get-FileHash -LiteralPath $new).Hash
        if($oldHash -eq $newHash){throw 'Fixtures are not distinct.'}
        $parent=Start-Process -FilePath $exe -ArgumentList '--fixture-update' -PassThru
        if(!$parent.WaitForExit(90000) -or $parent.ExitCode -ne 0){throw "Old app did not hand off. See $root/fixture-error.txt"}
        $clock=[Diagnostics.Stopwatch]::StartNew();$passed=$false
        while($clock.Elapsed.TotalSeconds -lt 100){
            $child=Get-Process -Name ChatGPT-PackagePicker -ErrorAction SilentlyContinue | Where-Object {$_.Id -ne $parent.Id -and $_.Path -eq $exe -and $_.MainWindowTitle -eq 'ChatGPT 安装助手'} | Select-Object -First 1
            if($child){
                $hash=(Get-FileHash -LiteralPath $exe).Hash
                if($rollback){$proof=Get-ChildItem -Path "$root/.picker-update-*/fixture-result.txt" -ErrorAction SilentlyContinue; $passed=$hash -eq $oldHash -and $proof -and ([IO.File]::ReadAllText($proof.FullName)).Contains('已恢复并重新打开原版本')}
                else {$passed=$hash -eq $newHash -and !(Get-ChildItem -LiteralPath $root -Directory -Force -Filter '.picker-update-*')}
                if($passed){Write-Host "PASS rollback=$rollback oldPID=$($parent.Id) restartedPID=$($child.Id) SHA256=$hash";break}
            }
            [Threading.Thread]::Sleep(200)
        }
        if(!$passed){throw "Real restart verification failed: $root"}
    } finally {
        # Never stop other assistants or search for/delete unrelated temporary directories.
        Get-Process -Name ChatGPT-PackagePicker,picker-update-helper -ErrorAction SilentlyContinue | Where-Object {$_.Path -and $_.Path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)} | ForEach-Object {$_.Kill();$_.WaitForExit(5000)|Out-Null;$_.Dispose()}
        if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force}
    }
}
