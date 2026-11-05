"""Single offline candidate-package smoke check. Does not load/call the Revit API."""
import hashlib
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

package = Path(sys.argv[1]).resolve()
report_path = Path(sys.argv[2])
checks = []

def require(value, description):
    if not value:
        raise SystemExit('FAIL: ' + description)
    checks.append(description)

hashes = json.loads((package / 'sha256.json').read_text(encoding='utf-8-sig'))
require(all(Path(n).name == n and hashlib.sha256((package/n).read_bytes()).hexdigest().upper() == h for n,h in hashes.items()), 'Every packaged file matches its SHA256 inventory')
require(set(p.name for p in package.iterdir() if p.is_file()) == set(hashes) | {'sha256.json'}, 'No unlisted package files')
require(not any(n.lower().startswith(('revitapi','autodesk.')) for n in hashes), 'Revit API/Autodesk binaries are not copied into the candidate')
settings = json.loads((package/'candidate-settings.json').read_text(encoding='utf-8-sig'))
require(Path(settings['TestRoot']) == Path(settings['WorkspaceRoot'])/'Revit_Test_Models'/'POC2StaticSite', 'Separate guarded POC2StaticSite root')
bootstrap = 'EVCanopyDesigner.Poc2.Revit.Bootstrap.' + settings['PackageId'] + '.dll'
manifest = ET.parse(next(package.glob('*.addin'))).getroot()
require(Path(manifest.findtext('./AddIn/Assembly')) == package/bootstrap and manifest.findtext('./AddIn/FullClassName') == 'EVCanopyDesigner.Poc2.Revit.Bootstrap.Poc2Application', 'Unique bootstrap and staged-only application manifest agree')
required = ['EVCanopyDesigner.Poc2.Revit.Host.dll','EVCanopyDesigner.Interactive.Revit.Host.dll','EVCanopyDesigner.Canopy.Core.dll','DesignerInteractive.Integration.dll','DesignerInteractive.Presentation.dll','EVCanopyDesigner.Interactive.UI.dll',bootstrap]
require(all(n in hashes and (package/n).read_bytes()[:2] == b'MZ' for n in required), 'Candidate and unchanged host dependencies are present as compiled assemblies')
config=json.loads((package/'selected-canopy.json').read_text())
fingerprint=config.pop('Fingerprint')
require(hashlib.sha256(json.dumps(config,separators=(',',':')).encode()).hexdigest().upper() == fingerprint == 'B6759D0DCF5EA3CEA989EF2B824F7890BB80F4E04FC94A6D9E46BC972F162B2F', 'Verified canopy codec fingerprint preserved')
scene=json.loads((package/'proposal.json').read_text())
require(scene['BaseFingerprint']==fingerprint and (scene['Site']['Width'],scene['Site']['Height'],scene['Road']['Height'])==(34.5,24.5,7) and len(scene['Gateways'])==2 and all(g['Bounds']['Width']==6 for g in scene['Gateways']), 'Accepted site, road and gateway dimensions bound to selected canopy')
require(len(scene['Spaces'])==10 and len(scene['Columns'])==4 and len(scene['Equipment']['Stations'])==5 and len({o['SpaceId'] for s in scene['Equipment']['Stations'] for o in s['Outputs']})==10, 'Ten spaces, four fixed supports, five reservation locations and ten output assignments')
review=json.loads((package/'maneuver-review.json').read_text())
require(scene['SiteLayoutApproved'] is False and review['SiteLayoutApproved'] is False and scene['HistoricalUnresolvedEquipmentAccessConflicts']==review['ExistingEquipmentAccessConflicts']==21 and review['AdditionalLandAssumedAvailable'] is True, 'All unresolved-conflict and additional-land limitations retained')
require(any(h['Kind']=='ParkedNeighbour' for r in review['Results'] for phase in r['Phases'] for h in phase['Findings']) and any(h['Kind']=='Boundary' for r in review['Results'] for phase in r['Phases'] for h in phase['Findings']), 'Representative maneuver failures remain in the package')
repo=Path(settings['WorkspaceRoot'])
for name, source in [('selected-canopy.json',repo/'SitePoc/Circulation/selected-canopy.json'),('proposal.json',repo/'docs/previews/POC2Circulation-20261026-refined/proposal.json'),('maneuver-review.json',repo/'docs/previews/POC2Circulation-20261026-final-review/review.json')]:
    require((package/name).read_bytes()==source.read_bytes(), name+' matches existing evidence byte for byte')
report_path.parent.mkdir(parents=True,exist_ok=True)
report_path.write_text(json.dumps(dict(Status='PASS',CheckCount=len(checks),Checks=checks,Package=str(package),PackageId=settings['PackageId'],PackageHashes=hashes,Scope='Offline package/input integrity only; Revit transaction, annotation, ownership reconciliation and rollback remain live-unverified',Deployed=False,RevitWrites=False),indent=2)+'\n')
print(f'PASS: {len(checks)} focused offline package checks; no Revit API execution. Report: {report_path}')
