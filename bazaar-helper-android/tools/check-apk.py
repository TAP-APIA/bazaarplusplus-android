#!/usr/bin/env python3
import sys,zipfile,io
with zipfile.ZipFile(sys.argv[1]) as z:
    names=z.namelist();assert 'classes.dex' in names
    for n in names:
        assert 'bazaarplusplus' not in n.lower(),n
        if n.endswith('.dex'):
            data=z.read(n)
            for forbidden in [b'Lcom/bepinex/android/',b'BazaarPlusPlus',b'BppAssistant']:assert forbidden not in data,(n,forbidden)
        if n.endswith('.zip'):
            with zipfile.ZipFile(io.BytesIO(z.read(n))) as nested:assert not any('bazaarplusplus' in s.lower() for s in nested.namelist()),n
    plugins=[n for n in names if n.endswith('.dll')];assert plugins==['assets/BazaarHelper.Hook.dll'],plugins
    assert 'lib/arm64-v8a/libhelperjni.so' in names
print('APK scope check passed: own DEX + only BazaarHelper hook + pinned foundation')
