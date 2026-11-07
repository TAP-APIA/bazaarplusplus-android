#!/usr/bin/env python3
"""SDK-only build: original source -> javac -> D8; pinned native framework -> CMake."""
import os,pathlib,subprocess,shutil,sys,zipfile,json,hashlib,argparse
ROOT=pathlib.Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('--interop',type=pathlib.Path,default=ROOT/'.local/game-interop');p.add_argument('--test',action='store_true');args=p.parse_args()
SDK=pathlib.Path(os.environ.get('ANDROID_HOME',pathlib.Path.home()/'Library/Android/sdk'))
JAVA=pathlib.Path(os.environ.get('JAVA_HOME',pathlib.Path.home()/'Library/Java/JavaVirtualMachines/ms-17.0.14/Contents/Home'))
# CI runs on Linux; the NDK ships the same toolchain layout per host.
HOST='darwin-x86_64' if sys.platform=='darwin' else 'linux-x86_64'
TOOLS=SDK/'build-tools/36.0.0';PLATFORM=SDK/'platforms/android-36/android.jar';CMAKE=SDK/'cmake/3.22.1/bin/cmake';NDK=SDK/'ndk/27.0.12077973'
BUILD=ROOT/'build';BUILD.mkdir(exist_ok=True)
def run(cmd,**kwargs):subprocess.run([str(x) for x in cmd],check=True,**kwargs)
run(['python3',ROOT/'tools/prepare-dependencies.py'])
if not (args.interop/'BazaarGameShared.dll').is_file():raise SystemExit('Provide current game interop directory with --interop (compile input only; never packaged)')
run(['dotnet','build',ROOT/'hook','-c','Release','-p:GameInterop='+str(args.interop.resolve())])
nb=ROOT/'.local/native-build';run([CMAKE,'-S',ROOT/'.local/foundation/cpp','-B',nb,'-DCMAKE_TOOLCHAIN_FILE='+str(NDK/'build/cmake/android.toolchain.cmake'),'-DANDROID_ABI=arm64-v8a','-DANDROID_PLATFORM=android-29','-DANDROID_STL=c++_static','-DCMAKE_BUILD_TYPE=Release']);run([CMAKE,'--build',nb,'-j','6'])
lib=BUILD/'native/arm64-v8a';shutil.copytree(ROOT/'.local/foundation/jniLibs/arm64-v8a',lib,dirs_exist_ok=True)
for name in ['main','nextbep']:shutil.copy2(nb/name/('lib'+name+'.so'),lib)
clang=NDK/f'toolchains/llvm/prebuilt/{HOST}/bin/aarch64-linux-android29-clang++'
run([clang,'-shared','-fPIC','-O2','-std=c++17','-static-libstdc++',ROOT/'app/src/main/cpp/bridge.cpp','-o',lib/'libhelperjni.so'])
assets=BUILD/'assets';shutil.copytree(ROOT/'.local/assets',assets,dirs_exist_ok=True);shutil.copy2(ROOT/'hook/bin/Release/net10.0/BazaarHelper.Hook.dll',assets)
res=BUILD/'compiled-res.zip';run([TOOLS/'aapt2','compile','--dir',ROOT/'app/src/main/res','-o',res])
manifest=BUILD/'AndroidManifest.xml';manifest.write_text((ROOT/'app/src/main/AndroidManifest.xml').read_text().replace('<manifest ','<manifest package="com.bazzarhelper.android" ',1))
unsigned=BUILD/'unsigned.apk';run([TOOLS/'aapt2','link','-o',unsigned,'--manifest',manifest,'-I',PLATFORM,'-R',res,'-A',assets,'--auto-add-overlay'])
classes=BUILD/'classes';shutil.rmtree(classes,ignore_errors=True);classes.mkdir()
sources=sorted((ROOT/'app/src/main/java').rglob('*.java'));run([JAVA/'bin/javac','-encoding','UTF-8','-source','17','-target','17','-classpath',PLATFORM,'-d',classes,*sources])
jar=BUILD/'app.jar';run([JAVA/'bin/jar','cf',jar,'-C',classes,'.'])
dex=BUILD/'dex';dex.mkdir(exist_ok=True);run([JAVA/'bin/java','-cp',TOOLS/'lib/d8.jar','com.android.tools.r8.D8','--release','--min-api','29','--lib',PLATFORM,'--output',dex,jar])
with zipfile.ZipFile(unsigned,'a',compression=zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for f in sorted(dex.glob('*.dex')):z.write(f,f.name)
    for f in sorted(lib.glob('*.so')):z.write(f,'lib/arm64-v8a/'+f.name)
# Release signing comes from the environment in CI (see .github/workflows/android.yml); the
# local dev keystore stays the default so `python3 tools/build.py` keeps working out of the box.
env_key=os.environ.get('ANDROID_KEYSTORE','')
key=pathlib.Path(env_key) if env_key else ROOT/'.keystore/development.p12'
key.parent.mkdir(parents=True,exist_ok=True)
key_pass=os.environ.get('ANDROID_KEYSTORE_PASSWORD','android')
alias=os.environ.get('ANDROID_KEY_ALIAS','bazaarhelper')
alias_pass=os.environ.get('ANDROID_KEY_PASSWORD',key_pass)
# apksigner picks the key itself when the store holds exactly one, so a CI release keystore needs
# ANDROID_KEY_ALIAS only when it carries several entries.
alias_args=([] if env_key and not os.environ.get('ANDROID_KEY_ALIAS')
            else ['--ks-key-alias',alias,'--key-pass','pass:'+alias_pass])
if not key.exists():
    if env_key:raise SystemExit(f'ANDROID_KEYSTORE points at a missing file: {key}')
    run([JAVA/'bin/keytool','-genkeypair','-keystore',key,'-storepass',key_pass,'-keypass',alias_pass,'-alias',alias,'-keyalg','RSA','-keysize','2048','-validity','10000','-dname','CN=BazaarHelper Development'])
aligned=BUILD/'aligned.apk';out=BUILD/'BazaarHelper-0.1.2.apk';run([TOOLS/'zipalign','-f','-p','4',unsigned,aligned]);run([TOOLS/'apksigner','sign','--ks',key,'--ks-pass','pass:'+key_pass,*alias_args,'--out',out,aligned],env={**os.environ,'JAVA_HOME':str(JAVA)})
run([TOOLS/'apksigner','verify','--verbose',out],env={**os.environ,'JAVA_HOME':str(JAVA)})
run(['python3',ROOT/'tools/check-apk.py',out])
manifest={'apk':out.name,'sha256':hashlib.sha256(out.read_bytes()).hexdigest(),'package':'com.bazzarhelper.android','gameInterop':{f.name:hashlib.sha256(f.read_bytes()).hexdigest() for f in sorted(args.interop.glob('*.dll'))},'foundation':json.loads((ROOT/'third-party/foundation.lock.json').read_text())['commit']}
(BUILD/'build-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(out)
if args.test:run(['python3',ROOT/'tools/test-device.py'])
