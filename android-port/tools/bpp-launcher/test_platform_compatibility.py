"""Compile and run the pure-Java HarmonyOS ANCO launch-policy tests."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


ROOT = Path(__file__).resolve().parent


def run():
    java_home = os.environ.get("JAVA_HOME")
    suffix = ".exe" if os.name == "nt" else ""
    javac = str(Path(java_home) / "bin" / ("javac" + suffix)) if java_home else shutil.which("javac")
    java = str(Path(java_home) / "bin" / ("java" + suffix)) if java_home else shutil.which("java")
    if not java or not javac:
        raise RuntimeError("Set JAVA_HOME to a JDK 17+ installation")
    package = Path("com/bepinex/android/bpp")
    with tempfile.TemporaryDirectory(prefix="bpp-platform-tests-") as output:
        subprocess.run([
            javac,
            "-encoding",
            "UTF-8",
            "--release",
            "11",
            "-d",
            output,
            str(ROOT / "src" / package / "BppPlatformCompatibility.java"),
            str(ROOT / "test" / package / "BppPlatformCompatibilityTests.java"),
        ], check=True)
        subprocess.run([
            java,
            "-cp",
            output,
            "com.bepinex.android.bpp.BppPlatformCompatibilityTests",
        ], check=True)


if __name__ == "__main__":
    run()
