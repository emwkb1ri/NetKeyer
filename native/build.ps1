$ErrorActionPreference = "Stop"

$buildDir = "build"
$destDir = "windows-x64"

function Remove-BuildDirIfPresent {
	if (Test-Path $buildDir) {
		Remove-Item -Recurse -Force $buildDir
	}
}

function Invoke-Checked {
	param(
		[Parameter(Mandatory = $true)]
		[string]$Description,

		[Parameter(Mandatory = $true)]
		[scriptblock]$Action
	)

	& $Action
	if ($LASTEXITCODE -ne 0) {
		throw "$Description failed with exit code $LASTEXITCODE."
	}
}

function Get-VsDevCmdPath {
	$base = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\2022"
	$candidates = @(
		(Join-Path $base "BuildTools\Common7\Tools\VsDevCmd.bat"),
		(Join-Path $base "Community\Common7\Tools\VsDevCmd.bat"),
		(Join-Path $base "Professional\Common7\Tools\VsDevCmd.bat"),
		(Join-Path $base "Enterprise\Common7\Tools\VsDevCmd.bat")
	)

	foreach ($path in $candidates) {
		if (Test-Path $path) {
			return $path
		}
	}

	return $null
}

function Invoke-InVsDevShell {
	param(
		[Parameter(Mandatory = $true)]
		[string]$Command,

		[Parameter()]
		[string]$VsDevCmd
	)

	if ([string]::IsNullOrWhiteSpace($VsDevCmd)) {
		$vsDevCmd = Get-VsDevCmdPath
	}
	else {
		$vsDevCmd = $VsDevCmd
	}

	if ([string]::IsNullOrWhiteSpace($vsDevCmd)) {
		throw "Could not find VsDevCmd.bat. Install Visual Studio 2022 Build Tools with C++ workload."
	}

	$cmdLine = "call `"$vsDevCmd`" -arch=x64 >nul && $Command"
	cmd /c $cmdLine

	if ($LASTEXITCODE -ne 0) {
		throw "Command failed in VS dev shell: $Command"
	}
}

# If an old cache exists with a different/broken generator, remove it.
if (Test-Path (Join-Path $buildDir "CMakeCache.txt")) {
	Remove-BuildDirIfPresent
}

$built = $false

$vsDevCmdPath = Get-VsDevCmdPath
if (-not [string]::IsNullOrWhiteSpace($vsDevCmdPath)) {
	try {
		Write-Host "Attempting Visual Studio 2022 generator..."
		Invoke-InVsDevShell "cmake -S . -B $buildDir -G `"Visual Studio 17 2022`" -A x64" -VsDevCmd $vsDevCmdPath
		Invoke-InVsDevShell "cmake --build $buildDir --config Release --target netkeyer_midi_shim" -VsDevCmd $vsDevCmdPath
		$built = $true
	}
	catch {
		Write-Warning "Visual Studio generator failed: $($_.Exception.Message)"
		Write-Host "Falling back to Ninja generators..."
		Remove-BuildDirIfPresent
	}
}

if (-not $built) {
	$ninja = Get-Command ninja -ErrorAction SilentlyContinue
	if ($null -eq $ninja) {
		throw "Visual Studio generator failed and ninja is not installed. Install Visual Studio 2022 Build Tools (Desktop development with C++) or install Ninja and a C/C++ toolchain."
	}

	$usedNinjaMultiConfig = $true
	try {
		Write-Host "Configuring with Ninja Multi-Config..."
		Invoke-Checked "CMake configure (Ninja Multi-Config)" { & cmake -S . -B $buildDir -G "Ninja Multi-Config" }
	}
	catch {
		$usedNinjaMultiConfig = $false
		Write-Warning "Ninja Multi-Config unavailable, retrying with Ninja single-config..."
		Remove-BuildDirIfPresent
		Invoke-Checked "CMake configure (Ninja)" { & cmake -S . -B $buildDir -G Ninja -DCMAKE_BUILD_TYPE=Release }
	}

	if ($usedNinjaMultiConfig) {
		Invoke-Checked "CMake build (Ninja Multi-Config)" { & cmake --build $buildDir --config Release --target netkeyer_midi_shim }
	}
	else {
		Invoke-Checked "CMake build (Ninja)" { & cmake --build $buildDir --target netkeyer_midi_shim }
	}

	$built = $true
}

New-Item -Force -ItemType Directory $destDir | Out-Null

$dllPathCandidates = @(
	(Join-Path $buildDir "Release\netkeyer_midi_shim.dll"),
	(Join-Path $buildDir "netkeyer_midi_shim.dll")
)

$dllPath = $dllPathCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (!(Test-Path $dllPath)) {

	$joined = [string]::Join("', '", $dllPathCandidates)
	throw "Build completed but DLL not found. Checked '$joined'."
}

Copy-Item $dllPath (Join-Path $destDir "netkeyer_midi_shim.dll") -Force

Write-Host "Native shim built and copied to $destDir\"
