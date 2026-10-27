from pathlib import Path
import hashlib,json,zipfile
root=Path(__file__).resolve().parents[1]
version="0.3.0"
exe=root/"dist/standalone/Edifier.exe"
assert exe.is_file()
source_files=[root/name for name in ("README.md","LICENSE","THIRD-PARTY-NOTICES.md","build.ps1",".gitignore")]
for folder in ("src","tests","docs","third-party","tools"):
    source_files += [p for p in (root/folder).rglob("*") if p.is_file() and not {"bin","obj","__pycache__","Probe","AncProbe"}.intersection(p.relative_to(root).parts)]
source=root/f"dist/Edifier-{version}-source.zip"
with zipfile.ZipFile(source,"w",zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for p in sorted(source_files):z.write(p,"edifier-manager/"+p.relative_to(root).as_posix())
payload={"Edifier.exe":exe,source.name:source}
for p in source_files:
    rel=p.relative_to(root).as_posix()
    if rel.split('/')[0] in {"docs","third-party"} or p.name in {"README.md","LICENSE","THIRD-PARTY-NOTICES.md"}:payload[rel]=p
manifest="".join(hashlib.sha256(p.read_bytes()).hexdigest()+"  "+name+"\n" for name,p in sorted(payload.items()))
package=root/f"dist/Edifier-Desktop-{version}-win-x64.zip"
with zipfile.ZipFile(package,"w",zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for name,p in sorted(payload.items()):z.write(p,name)
    z.writestr("SHA256SUMS.txt",manifest)
for path in [source,package]:
    with zipfile.ZipFile(path) as z:
        assert z.testzip() is None
        assert not any(part in entry.split('/') for entry in z.namelist() for part in ("artifacts","research","backups","EdifierManager-data"))
receipt={"version":version,"file":package.name,"bytes":package.stat().st_size,"sha256":hashlib.sha256(package.read_bytes()).hexdigest(),"crc_check":"passed","source_included":True,"private_backups_excluded":True}
(root/"dist/package-receipt-0.3.0.json").write_text(json.dumps(receipt,indent=2),encoding="utf-8")
print(json.dumps(receipt,indent=2))
