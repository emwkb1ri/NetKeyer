$ErrorActionPreference = "Stop"

$buildDir = "build"
$destDir = "windows-x64"

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
		[string]$Command
	)

	$vsDevCmd = Get-VsDevCmdPath
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
	Remove-Item -Recurse -Force $buildDir
}

Invoke-InVsDevShell "cmake -S . -B $buildDir -G `"Visual Studio 17 2022`" -A x64"
Invoke-InVsDevShell "cmake --build $buildDir --config Release --target netkeyer_midi_shim"

New-Item -Force -ItemType Directory $destDir | Out-Null

$dllPath = Join-Path $buildDir "Release\netkeyer_midi_shim.dll"
if (!(Test-Path $dllPath)) {
	throw "Build completed but DLL not found at '$dllPath'."
}

Copy-Item $dllPath (Join-Path $destDir "netkeyer_midi_shim.dll") -Force

Write-Host "Native shim built and copied to $destDir\"
