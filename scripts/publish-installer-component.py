#!/usr/bin/env python3
"""Extend the deployment skill's SDK publisher with declared Console assets."""
import importlib.util
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

sys.dont_write_bytecode = True
skill = Path.home()/'.codex/skills/create-dotnet-deployment-package/scripts/publish_dotnet.py'
if not skill.is_file(): raise SystemExit('Deployment skill publisher is required on this developer workstation.')
spec = importlib.util.spec_from_file_location('skill_publish', skill)
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)
original_filter = publisher.runtime_filter

def retain_assets(path, app):
    original_filter(path, app)
    if app == 'IISManager.Console':
        tree = ET.parse(path)
        item = tree.find('./Target/ItemGroup/ResolvedFileToPublish')
        item.set('Condition', item.get('Condition') +
                 " and !$([System.String]::Copy('%(ResolvedFileToPublish.RelativePath)').StartsWith('wwwroot/'))" +
                 " and !$([System.String]::Copy('%(ResolvedFileToPublish.RelativePath)').StartsWith('wwwroot\\'))")
        tree.write(path, encoding='utf-8', xml_declaration=False)

publisher.runtime_filter = retain_assets
project = Path(sys.argv[sys.argv.index('--project')+1]).resolve()
stage = Path(sys.argv[sys.argv.index('--stage')+1]).resolve()
assets = sorted((project.parent/'wwwroot').rglob('*'))
before = {p.relative_to(project.parent).as_posix():publisher.digest(p) for p in assets if p.is_file()}
publisher.main()
after = {p.relative_to(project.parent).as_posix():publisher.digest(p) for p in assets if p.is_file()}
if before != after: raise SystemExit('Static source changed during SDK publish.')
for relative,fingerprint in before.items():
    published = stage/'publish'/relative
    if not published.is_file() or publisher.digest(published)!=fingerprint: raise SystemExit('SDK static resource publication mismatch.')
evidence_path = stage/'publish-evidence.json'
evidence = json.loads(evidence_path.read_text())
evidence['staticAssetsPublishedBySdk'] = before
evidence['staticAssetsStableDuringPublish'] = True
evidence_path.write_text(json.dumps(evidence,ensure_ascii=False,indent=2)+'\n')
