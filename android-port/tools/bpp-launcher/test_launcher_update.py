"""Run the production checker on a JVM; org.json is a pinned, host-test-only dependency."""
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parent
JSON_SHA256 = "3cf6cd6892e32e2b4c1c39e0f52f5248a2f5b37646fdfbb79a66b46b618414ed"
JSON_URL = "https://repo.maven.apache.org/maven2/org/json/json/20240303/json-20240303.jar"


def run():
    java_home = os.environ.get("JAVA_HOME")
    suffix = ".exe" if os.name == "nt" else ""
    javac = str(Path(java_home) / "bin" / ("javac" + suffix)) if java_home else shutil.which("javac")
    java = str(Path(java_home) / "bin" / ("java" + suffix)) if java_home else shutil.which("java")
    if not java or not javac:
        raise RuntimeError("Set JAVA_HOME to a JDK 17+ installation")
    cache = Path(os.environ.get("BPP_TEST_CACHE", tempfile.gettempdir())) / "bpp-launcher-test-deps"
    cache.mkdir(parents=True, exist_ok=True)
    jar = cache / "json-20240303.jar"
    if not jar.exists() or hashlib.sha256(jar.read_bytes()).hexdigest() != JSON_SHA256:
        with urllib.request.urlopen(JSON_URL, timeout=30) as response:
            data = response.read(256 * 1024)
        if hashlib.sha256(data).hexdigest() != JSON_SHA256:
            raise RuntimeError("org.json host-test dependency checksum mismatch")
        jar.write_bytes(data)
    with tempfile.TemporaryDirectory(prefix="bpp-update-tests-") as output:
        package = Path("com/bepinex/android/bpp")
        subprocess.run([javac, "-encoding", "UTF-8", "--release", "11", "-cp", str(jar), "-d", output,
                        str(ROOT / "src" / package / "BppLauncherUpdateChecker.java"),
                        str(ROOT / "test" / package / "BppLauncherUpdateCheckerTests.java")], check=True)
        subprocess.run([java, "-cp", output + os.pathsep + str(jar),
                        "com.bepinex.android.bpp.BppLauncherUpdateCheckerTests", *sys.argv[1:]], check=True)


if __name__ == "__main__":
    run()
