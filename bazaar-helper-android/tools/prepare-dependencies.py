#!/usr/bin/env python3
"""Fetch only the pinned generic native/runtime foundation; never copy a launcher DEX or BPP."""
import hashlib,json,pathlib,subprocess,shutil,zipfile,os,urllib.request
ROOT=pathlib.Path(__file__).resolve().parents[1]
PIN='44e1e59c40e6f52df45139db2c86ad4499d2881d'
repo=ROOT/'.local/nextbep';repo.parent.mkdir(exist_ok=True)
if not repo.exists():
    source=pathlib.Path('/tmp/bazaarhelper-nextbep-reference')
    if source.exists() and subprocess.check_output(['git','-C',str(source),'rev-parse','HEAD'],text=True).strip()==PIN: shutil.copytree(source,repo)
    else:
        cmd=['git'];proxy=os.getenv('HTTPS_PROXY')
        if proxy:cmd+=['-c','http.proxy='+proxy]
        subprocess.run(['git','init',str(repo)],check=True,stdout=subprocess.DEVNULL)
        subprocess.run(['git','-C',str(repo),'remote','add','origin','https://github.com/NextBep/BepInEx.Android.Launcher.git'],check=True)
        subprocess.run(cmd+['-C',str(repo),'fetch','--depth=1','origin',PIN],check=True)
subprocess.run(['git','-C',str(repo),'checkout','--detach',PIN],check=True,stdout=subprocess.DEVNULL)
assert subprocess.check_output(['git','-C',str(repo),'rev-parse','HEAD'],text=True).strip()==PIN
src=repo/'app/src/main';dest=ROOT/'.local/foundation';dest.mkdir(exist_ok=True)
for folder in ['cpp','jniLibs']:
    for item in (src/folder).rglob('*'):
        if not item.is_file():continue
        target=dest/folder/item.relative_to(src/folder)
        if not target.is_file() or target.read_bytes()!=item.read_bytes():
            # Redirected C++ files are handled below; copy pristine input before patching.
            if target.suffix in ['.cpp','.h'] and target.exists():
                expected=item.read_text().replace('com/bepinex/android/tools/FusionConfig','com/bazzarhelper/android/runtime/RuntimeConfig').replace('com/bepinex/android/tools/JniBridge','com.bazzarhelper.android.runtime.RuntimeCallbacks').replace('nextbep.config','bazaarhelper.runtime.config')
                if target.read_text()==expected:continue
            target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(item,target)
# Native ABI is redirected to independent app classes; upstream Java/UI is never compiled.
for p in (dest/'cpp').rglob('*'):
    if p.suffix not in ['.cpp','.h']:continue
    text=p.read_text();text=text.replace('com/bepinex/android/tools/FusionConfig','com/bazzarhelper/android/runtime/RuntimeConfig').replace('com/bepinex/android/tools/JniBridge','com.bazzarhelper.android.runtime.RuntimeCallbacks').replace('nextbep.config','bazaarhelper.runtime.config')
    if p.read_text()!=text:p.write_text(text)
assets=ROOT/'.local/assets';assets.mkdir(exist_ok=True)
for name in ['dotnet.zip','BepInEx.Android.zip']:shutil.copy2(src/'assets'/name,assets/name)
with zipfile.ZipFile(assets/'BepInEx.Android.zip') as z:z.extractall(ROOT/'.local/managed')
files={str(p.relative_to(src)):hashlib.sha256(p.read_bytes()).hexdigest() for base in ['cpp','jniLibs','assets'] for p in (src/base).rglob('*') if p.is_file()}
lock={'repository':'https://github.com/NextBep/BepInEx.Android.Launcher','commit':PIN,'files':files}
lockpath=ROOT/'third-party/foundation.lock.json'
if lockpath.exists():assert json.loads(lockpath.read_text())==lock,'Pinned dependency content changed'
else:lockpath.write_text(json.dumps(lock,indent=2)+'\n')
# Unity base libraries ride inside the APK (third-party/unity-libs.json) so the first launch never
# has to fetch them; pin the bytes exactly like every other build input.
pin=json.loads((ROOT/'third-party/unity-libs.json').read_text())
unity=assets/'unity-libs';unity.mkdir(exist_ok=True)
# Shipped under the game's Unity version so the runtime lookup is an exact filename match.
wanted={item['game']+'.zip' for item in pin['entries']}
for stale in unity.glob('*.zip'):
    if stale.name not in wanted:stale.unlink();print('Dropped stale Unity base libraries',stale.name)
proxy=os.getenv('HTTPS_PROXY')
opener=urllib.request.build_opener(urllib.request.ProxyHandler({'https':proxy,'http':proxy})) if proxy else urllib.request.build_opener()
for item in pin['entries']:
    target=unity/(item['game']+'.zip')
    if target.is_file() and hashlib.sha256(target.read_bytes()).hexdigest()==item['sha256']:continue
    url=pin['source'].replace('{version}',item['version']);tmp=target.with_name(target.name+'.tmp')
    # unity.bepinex.dev rejects Python's default user agent (403), so identify ourselves.
    req=urllib.request.Request(url,headers={'User-Agent':'BazaarHelper-build'})
    with opener.open(req,timeout=600) as r,open(tmp,'wb') as f:shutil.copyfileobj(r,f)
    assert hashlib.sha256(tmp.read_bytes()).hexdigest()==item['sha256'],'Unity base libraries changed: '+url
    tmp.replace(target);print('Fetched Unity base libraries',item['version'],'as',target.name)
print('Verified generic foundation',PIN,'(no upstream app Java/DEX)')
