"""Check the single EXE without an external .NET installation or device access."""
from pathlib import Path
from io import BytesIO
import datetime
import hashlib
import json
import os
import shutil
import subprocess
import time
import uuid
import pefile
from PIL import Image

root = Path(__file__).resolve().parents[1]
exe = root / "dist/standalone/Edifier.exe"
pe = pefile.PE(str(exe))
icons = []
groups = 0
for entry in pe.DIRECTORY_ENTRY_RESOURCE.entries:
    if entry.id == 14:
        groups += len(entry.directory.entries)
    if entry.id == 3:
        for item in entry.directory.entries:
            data = item.directory.entries[0].data.struct
            with Image.open(BytesIO(pe.get_data(data.OffsetToData, data.Size))) as icon:
                icons.append(icon.size[0])
assert sorted(icons) == [16, 20, 24, 32, 40, 48, 64, 128, 256], icons
assert groups >= 1
pe.close()
trial = root / "artifacts" / ("portable-check-" + uuid.uuid4().hex[:8])
app = trial / "app"
app.mkdir(parents=True)
shutil.copy2(exe, app / "Edifier.exe")
assert [p.name for p in app.iterdir()] == ["Edifier.exe"]
output = trial / "preview"
extract = trial / "bundle-extract"
empty = trial / "no-dotnet"
empty.mkdir()
env = os.environ.copy()
for key in ["DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT(x86)"]:
    env[key] = str(empty)
env["DOTNET_MULTILEVEL_LOOKUP"] = "0"
env["PATH"] = str(Path(os.environ["SystemRoot"]) / "System32")
env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = str(extract)
env["EDIFIER_PREVIEW_DIR"] = str(output)
env["DOTNET_HOST_TRACE"] = "1"
env["DOTNET_HOST_TRACEFILE"] = str(trial / "host-trace.txt")
started = time.perf_counter()
result = subprocess.run([str(app / "Edifier.exe"), "--preview-ui", "--verify-native"], cwd=app, env=env, timeout=60, capture_output=True, text=True)
assert result.returncode == 0, (result.returncode, result.stdout, result.stderr)
report = json.loads((output / "native-verification.json").read_text(encoding="utf-8"))
assert report["passed"] and report["licenses"] == 8
assert not report["bluetoothAccessed"] and not report["audioAccessed"]
assert len(report["captures"]) == 33 and "welcome-light.png" in report["captures"]
w820, lolli, w830 = report["layouts"]
assert len(w830["tabs"]) == 3 and w830["multipointVisible"] and not w830["touchVisible"] and not w830["cycleVisible"]
assert lolli["multipointVisible"] and not w820["multipointVisible"]
assert len(w820["tabs"]) == 3 and w820["cycleVisible"] and not w820["touchVisible"] and not w820["wearVisible"]
assert len(lolli["tabs"]) == 4 and not lolli["cycleVisible"] and lolli["touchVisible"] and lolli["wearVisible"]
assert lolli["multipointGuideVisible"] and not lolli["multipointSwitchVisible"]
trace = (trial / "host-trace.txt").read_text(encoding="utf-8")
assert "Executing as a self-contained app" in trace
assert "is_framework_dependent=0" in trace
assert "Completed new extraction." in trace
native_files = sorted(p.name for p in extract.rglob("*.dll"))
assert {"PresentationNative_cor3.dll", "wpfgfx_cor3.dll", "D3DCompiler_47_cor3.dll"}.issubset(native_files)
ui = root / "docs/ui"
ui.mkdir(exist_ok=True)
for name in report["captures"]:
    path = output / name
    with Image.open(path) as screenshot:
        screenshot.verify()
    assert path.stat().st_size > 1000
    shutil.copy2(path, ui / name)
shutil.copy2(output / "native-verification.json", root / "docs/native-verification.json")
receipt = {
    "version": "0.3.0", "at": datetime.datetime.now().astimezone().isoformat(),
    "exe_sha256": hashlib.sha256(exe.read_bytes()).hexdigest(), "exit_code": result.returncode,
    "elapsed_seconds": round(time.perf_counter() - started, 2), "only_exe_copied": True,
    "dotnet_root_empty": True, "host_confirmed_self_contained": True, "framework_dependent": False,
    "extracted_native_files": native_files, "icon_sizes": sorted(icons), "icon_groups": groups,
    "embedded_license_documents": report["licenses"], "previews_generated": len(report["captures"]),
    "model_layouts": report["layouts"], "hardware_access": False, "hardware_verified": False,
}
(root / "docs/portable-check-0.3.0.json").write_text(json.dumps(receipt, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(json.dumps(receipt, indent=2, ensure_ascii=False))
