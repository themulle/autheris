# Third-party notices

Autheris ships no third-party test corpus, fuzzing tool or container image in its repository or its release artifacts.
The tools below are used only by the nightly simulation suites (`.github/workflows/simulation-nightly.yml`), which are not part of any solution,
release build or container image. Nothing listed here is committed to this repository.

| Component | Licence | How it is obtained | Where it is used |
|---|---|---|---|
| sqllogictest | MIT (elected, see below) | Fetched at job time from a pinned commit of `github.com/gregrahn/sqllogictest` (commit `c93ac416f05635ff5cc490009add936d9753d01d`), each file verified by sha256 against `tests/Autheris.Tests.Simulation/Corpus/sqllogictest.manifest.json`; written to `$RUNNER_TEMP` only | `sim-corpus` jobs |
| Schemathesis | MIT | Container image `schemathesis/schemathesis`, pinned by version and digest in the workflow (`SCHEMATHESIS_IMAGE`) | `sim-api-fuzz` job |
| SharpFuzz | MIT | NuGet package `SharpFuzz` (version pinned in `tests/Autheris.Tests.Fuzz/Autheris.Tests.Fuzz.csproj`) and the `SharpFuzz.CommandLine` dotnet tool (exact version pinned in `scripts/sim/run-parser-fuzz.sh`) | `sim-parser-fuzz` jobs |
| AFL++ | Apache-2.0 (licence of the installed `ubuntu-24.04` distro package, version `4.09c-1` in the noble archive; upstream relicensed to AGPL-3.0-or-later in 2026-06) | Distro package `afl++` from the `ubuntu-24.04` runner archive (distro-signed; the job logs `dpkg-query` and `apt-cache policy afl++`) | `sim-parser-fuzz` jobs |

## sqllogictest licence election

sqllogictest is by D. Richard Hipp (`COPYRIGHT.md` of the upstream repository). The author grants the user a choice of the GPL, BSD, MIT or CC0
licence and requires no attribution. Autheris elects the **MIT** licence. Because the grant's scope text refers to "this program" and does not
name the test files explicitly, no sqllogictest file is committed here; the files are downloaded at job time and used only as data.

Provenance: the upstream tree on sqlite.org blocks non-browser clients, so the pinned mirror `github.com/gregrahn/sqllogictest` is used.

## AFL++ licence note

AFL++ is used as an unmodified external process on the CI runner only; nothing of it is distributed or linked. The installed version named above must be re-checked at each Ubuntu runner image change. A distro bump to an AGPL-licensed package is acceptable only while the tool stays unmodified, external and undistributed.
