param(
    [string[]]$Frameworks = @('net10.0'),
    [switch]$Legacy,
    [switch]$Interop
)
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path -Parent $PSScriptRoot
$config = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'verification.json') | ConvertFrom-Json
Push-Location -LiteralPath $repoPath
try {
    if ($config.interop) {
        $manifest = Get-Content -Raw eng/runtime-sha256.json | ConvertFrom-Json
        $runtimeFiles = @(Get-ChildItem Runtime -Filter '*.cs')
        if ($runtimeFiles.Count -ne @($manifest.PSObject.Properties).Count) { throw 'Runtime file manifest drifted' }
        foreach ($entry in $manifest.PSObject.Properties) {
            if (([Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText((Join-Path $repoPath "Runtime/$($entry.Name)")).Replace("`r`n", "`n"))))) -ne $entry.Value) { throw "Runtime parity manifest mismatch: $($entry.Name)" }
        }
    }
    dotnet build "$($config.assembly).csproj" -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Six-target runtime build failed' }
    foreach ($framework in $Frameworks) {
        dotnet test "Tests/$($config.assembly).Tests.csproj" -c Release -f $framework --logger "trx;LogFileName=$framework.trx" --results-directory TestResults
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $framework" }
        [xml]$result = Get-Content -Raw -LiteralPath "TestResults/$framework.trx"
        if ([int]$result.TestRun.ResultSummary.Counters.executed -lt $config.minTests -or [int]$result.TestRun.ResultSummary.Counters.failed -ne 0) { throw 'Missing tests or test failures' }
    }
    dotnet pack "$($config.assembly).csproj" -c Release --no-build -o artifacts --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Package failed' }
    $packageFile = Join-Path $repoPath "artifacts/$($config.package).$($config.version).nupkg"
    $packageHash = (Get-FileHash -LiteralPath $packageFile -Algorithm SHA256).Hash
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($packageFile)
    try {
        foreach ($target in @('netstandard2.0','netstandard2.1','net462','net8.0','net9.0','net10.0')) {
            if (!$zip.GetEntry("lib/$target/$($config.assembly).dll")) { throw "Missing package target: $target" }
        }
        if (!$zip.GetEntry('README.md')) { throw 'Package README missing' }
        if (@($zip.Entries | Where-Object { $_.FullName -match '(?i)(tests|consumer)\.dll$' }).Count) { throw 'Test binaries leaked into package' }
        $nuspec = $zip.GetEntry("$($config.package).nuspec")
        $reader = [System.IO.StreamReader]::new($nuspec.Open())
        try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($metadata.package.metadata.id -ne $config.package -or $metadata.package.metadata.version -ne $config.version -or $metadata.package.metadata.license.InnerText -ne 'MIT') { throw 'Invalid NuGet metadata' }
    } finally { $zip.Dispose() }

    # A fresh, content-keyed cache prevents accidentally consuming an older local package.
    $consumerCache = Join-Path $repoPath "artifacts/consumer-packages/$packageHash"
    $consumerProject = "$($config.compat)/Consumer.csproj"
    function Test-Consumer([string]$framework, [string]$language, [string]$asset = '') {
        dotnet build $consumerProject -c Release -t:Rebuild "-p:ConsumerFramework=$framework" "-p:LangVersion=$language" "-p:ExternalAsset=$asset" "-p:RestoreAdditionalProjectSources=$repoPath/artifacts" "-p:RestorePackagesPath=$consumerCache" --nologo
        if ($LASTEXITCODE -ne 0) { throw "Package consumer compile failed: $framework / C#$language / $asset" }
        if ($framework -eq 'net462') { & "./$($config.compat)/bin/Release/net462/Consumer.exe" }
        else { dotnet "./$($config.compat)/bin/Release/$framework/Consumer.dll" }
        if ($LASTEXITCODE -ne 0) { throw "Package consumer execution failed: $framework / C#$language / $asset" }
    }
    foreach ($framework in $Frameworks) {
        Test-Consumer $framework 'latest'
        if ($framework -ne 'net462') {
            foreach ($standard in @('netstandard2.0', 'netstandard2.1')) {
                $asset = Join-Path $consumerCache "$($config.package.ToLowerInvariant())/$($config.version)/lib/$standard/$($config.assembly).dll"
                Test-Consumer $framework 'latest' $asset
            }
        }
    }
    if ($Legacy) {
        if (!$IsWindows) { throw 'Legacy execution requires Windows with .NET Framework installed' }
        foreach ($language in @('1','2','3','4','5','6','7','7.1','7.2','7.3','8','9','10','11','12','13','14')) {
            if ([double]$language -ge $config.minLanguage) { Test-Consumer 'net462' $language }
        }
    }
    if ($Interop) {
        if (!$config.interop) { throw 'This library has no Go wire fixture' }
        Push-Location -LiteralPath "$($config.interop)/Go"
        try {
            go run . -verify ../vectors.txt
            if ($LASTEXITCODE -ne 0) { throw 'Checked-in Go vectors drifted' }
            foreach ($framework in $Frameworks) {
                go run . -verify "../../Tests/bin/Release/$framework/csharp-vectors.txt"
                if ($LASTEXITCODE -ne 0) { throw "C# to Go wire mismatch: $framework" }
            }
        } finally { Pop-Location }
    }
    Write-Output "PASS $($config.package) $($config.version) : build, discovered tests, package metadata and installed consumers"
} finally { Pop-Location }
