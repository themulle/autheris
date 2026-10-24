// REQ-SIMTEST WP0 usage stub. WP3 replaces this file with the real modes (--afl, --replay, --export-seeds, --export-dict).
const string Usage = """
    Autheris.Tests.Fuzz: SharpFuzz harness for TrinoSqlEngine (simulation testing only).

    Modes (implemented by WP3):
      --afl                         AFL++ out-of-process target
      --replay <file|dir>...        replay inputs in-process, exit 1 on a crash or hang
      --export-seeds <dir>          export the seed corpus
      --export-dict <file>          export the AFL dictionary

    Local run (no AFL needed):
      dotnet run --project tests/Autheris.Tests.Fuzz -- --replay tests/Autheris.Tests.Fuzz/corpus tests/Autheris.Tests.Fuzz/findings
    """;

Console.WriteLine(Usage);
return args.Length == 0 ? 0 : 2;
