"""Downloads the Delta Lake jars for Spark 4.0 and verifies their SHA-256 (image build time only, see Dockerfile)."""
import hashlib
import os
import sys
import urllib.request

BASE = "https://repo1.maven.org/maven2/io/delta/"
JARS = {
    "delta-spark_2.13-4.0.0.jar": (
        BASE + "delta-spark_2.13/4.0.0/delta-spark_2.13-4.0.0.jar",
        "538511702aae0ef6973a6a70af3d4543c9009f8edbed786a00737e2d3cd7f04e",
    ),
    "delta-storage-4.0.0.jar": (
        BASE + "delta-storage/4.0.0/delta-storage-4.0.0.jar",
        "9bdb9fb450f1e119eba53feb427f331b0d09072d26485b8273883ad72c9a2e1d",
    ),
}

target = sys.argv[1]
for name, (url, expected) in JARS.items():
    with urllib.request.urlopen(url, timeout=120) as response:  # noqa: S310 (https, fixed host)
        data = response.read()
    actual = hashlib.sha256(data).hexdigest()
    if actual != expected:
        sys.exit("SHA-256 mismatch for %s: expected %s, got %s" % (name, expected, actual))
    with open(os.path.join(target, name), "wb") as handle:
        handle.write(data)
    print("verified", name, actual)
