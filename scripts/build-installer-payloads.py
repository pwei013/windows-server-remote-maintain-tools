#!/usr/bin/env python3
"""Build verified self-contained Windows payloads using existing skill helpers."""
import argparse
import hashlib
import importlib.util
import json
import subprocess
import sys
import zipfile
from pathlib import Path

sys.dont_write_bytecode = True
parser = argparse.ArgumentParser()
parser.add_argument('--dotnet',default='dotnet')
parser.add_argument('--stage',required=True,help='A new task-owned staging directory')
parser.add_argument('--legacy-package',required=True,help='Previously verified first-release package')
parser.add_argument('--component',choices=('Agent','Console','both'),default='both')
parser.add_argument('--payload-directory',help='A new payload directory; preserve previous installer inputs')
args=parser.parse_args()
root=Path(__file__).resolve().parent.parent
stage=Path(args.stage).resolve()
if stage.exists(): raise SystemExit('Stage exists; select a new directory.')
legacy=Path(args.legacy_package).resolve()
skill=Path.home()/'.codex/skills/create-dotnet-deployment-package/scripts/package_dotnet.py'
spec=importlib.util.spec_from_file_location('skill_pack',skill)
packer=importlib.util.module_from_spec(spec);spec.loader.exec_module(packer)
payload_root=Path(args.payload_directory).resolve() if args.payload_directory else root/'work/installer-payload'
components=('Agent','Console') if args.component=='both' else (args.component,)
if any((payload_root/(component+'.zip')).exists() for component in components):
    raise SystemExit('Selected payload exists; do not overwrite it.')
stage.mkdir(parents=True)
payload_root.mkdir(parents=True,exist_ok=True)
for component in components:
    # Finder metadata in the historical baseline is not an installation input.
    packer.verify_package(legacy/component,ignore_metadata=True)
    component_stage=stage/component
    command=[sys.executable,str(root/'scripts/publish-installer-component.py'),
             '--project',str(root/'src'/('IISManager.'+component)/('IISManager.'+component+'.csproj')),
             '--stage',str(component_stage),'--dotnet',args.dotnet,'--tfm','net10.0','--rid','win-x64',
             '--configuration','Release','--self-contained','true']
    subprocess.run(command,check=True,cwd=root)
    published=component_stage/'publish'
    info=packer.discover(published,'net10.0','win-x64',True,['wwwroot'] if component=='Console' else [])
    records=[dict(e,kind='Runtime') for e in packer.entries_for(info['files']).values()]
    templates={name:(root/'src'/('IISManager.'+component)/name).read_bytes() for name in ('appsettings.json','NLog.config')}
    if component=='Console':
        defaults=json.loads(templates['appsettings.json'])
        # ASP.NET configuration arrays merge by index; the local allowlists
        # must not retain additional entries from development defaults.
        defaults['Console']['AllowedHosts']=[]
        defaults['Console']['AllowedClientNetworks']=[]
        templates['appsettings.json']=(json.dumps(defaults,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
    for name,data in templates.items():
        records.append({'path':name,'size':len(data),'sha256':hashlib.sha256(data).hexdigest().upper(),'kind':'DefaultConfiguration'})
    previous=json.loads((legacy/component/'PUBLISH-FILES.json').read_text())['files']
    manifest={'component':component,'targetFramework':'net10.0','runtimeIdentifier':'win-x64','selfContained':True,
              'files':records,'legacyRuntimeFiles':[dict(e,kind='Runtime') for e in previous]}
    archive_path=payload_root/(component+'.zip')
    with zipfile.ZipFile(archive_path,'x',compression=zipfile.ZIP_DEFLATED,compresslevel=9) as archive:
        for name,path in sorted(info['files'].items()): archive.write(path,name)
        for name,data in templates.items(): archive.writestr(name,data)
        archive.writestr('payload-manifest.json',json.dumps(manifest,ensure_ascii=False,indent=2)+'\n')
    with zipfile.ZipFile(archive_path) as archive:
        expected={e['path']:e for e in records}
        if set(archive.namelist()) != set(expected)|{'payload-manifest.json'}: raise SystemExit('ZIP file set mismatch.')
        for name,record in expected.items():
            data=archive.read(name)
            if len(data)!=record['size'] or hashlib.sha256(data).hexdigest().upper()!=record['sha256']: raise SystemExit('ZIP content mismatch.')
    evidence={'component':component,'runtimeFiles':len(info['files']),'nativeArchitecture':info['arch'],
              'mainDllPdb':info['pdbPair'],'payloadSha256':packer.file_hash(archive_path)[1],
              'payloadSize':archive_path.stat().st_size,'publish':json.loads((component_stage/'publish-evidence.json').read_text())}
    (payload_root/(component+'-evidence.json')).write_text(json.dumps(evidence,ensure_ascii=False,indent=2)+'\n')
    print(json.dumps({k:evidence[k] for k in ('component','runtimeFiles','payloadSize','payloadSha256')},ensure_ascii=False))
