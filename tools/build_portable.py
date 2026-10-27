"""Build a self-contained single EXE. The .NET 10 SDK is a build dependency only."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import shutil
import subprocess

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--offline-feed", type=Path, help="Optional folder of cached NuGet packages")
args = parser.parse_args()
artifacts = root / "artifacts/portable-build"
artifacts.mkdir(parents=True, exist_ok=True)
env = os.environ.copy()
for key, subdir in {"DOTNET_CLI_HOME":"dotnet-home", "NUGET_HTTP_CACHE_PATH":"nuget-http", "NUGET_PLUGINS_CACHE_PATH":"nuget-plugins", "NUGET_SCRATCH":"nuget-scratch"}.items():
    env[key] = str(artifacts / subdir)
env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
env["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false"
env["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
project = root / "src/EdifierManager/EdifierManager.csproj"
output = root / "dist/standalone"
command = ["dotnet", "publish", str(project), "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-o", str(output),
    "-p:TargetLatestRuntimePatch=true", "-p:AssemblyName=Edifier", "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=embedded", "-p:DebugSymbols=false", "-p:PublishTrimmed=false", "-p:PublishReadyToRun=false",
    "-p:RestorePackagesPath="+str(artifacts/"packages"), "-p:UseSharedCompilation=false", "-nodeReuse:false", "-v", "minimal"]
if args.offline_feed:
    command += ["--source", str(args.offline_feed.resolve()), "-p:NuGetAudit=false"]
subprocess.run(command, cwd=root, env=env, check=True)
files = sorted(p.name for p in output.iterdir() if p.is_file())
assert files == ["Edifier.exe"], files
runtime = json.loads((root / "src/EdifierManager/bin/Release/net10.0-windows10.0.19041.0/win-x64/Edifier.runtimeconfig.json").read_text())
frameworks = runtime["runtimeOptions"]["includedFrameworks"]
assert {f["name"] for f in frameworks} == {"Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"}
assert all(f["version"] == "10.0.14" for f in frameworks)
exe = output / "Edifier.exe"
receipt = {"version":"0.3.0", "executable":"dist/standalone/Edifier.exe", "bytes":exe.stat().st_size,
    "sha256":hashlib.sha256(exe.read_bytes()).hexdigest(), "self_contained":True, "distribution_file_count":1,
    "runtime_identifier":"win-x64", "included_frameworks":frameworks, "native_libraries_embedded":True,
    "runtime_install_required":False, "trimming":False, "icon_embedded":True}
(root / "docs/portable-build-0.3.0.json").write_text(json.dumps(receipt, indent=2)+"\n", encoding="utf-8")
print(json.dumps(receipt, indent=2))
