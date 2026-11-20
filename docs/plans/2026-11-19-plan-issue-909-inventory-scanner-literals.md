# Plan: security test inventory scanner ignores separators inside literals (Issue #909)

Phase 2 (solution architect), 2026-11-19. Code facts verified on `origin/main` `e3e32b401`. Implementation starts after the v1.9.0 tag.

## 1. Problem and evidence

- Issue: [#909](https://github.com/themulle/autheris/issues/909). Found in the review of WP-SIMPL-29 PR 2b, which changed its test data to work around the bug.
- `tests/Autheris.Tests.Architecture/TestCoverageMapTests.cs` defines `TestInventoryScanner.ExtractFromSource` (line 42).
  - The class attribute window is cut at the last `}` or `;` (lines 68-72).
  - The method attribute window is cut at the last `}`, `;` or `{` (lines 101-106).
  - The scanner works on raw text. A `;`, `{` or `}` inside a string literal, char literal or comment (for example `[InlineData("a;b")]`) cuts the window after `[Theory]` or `[Trait("Category","Security")]`. The test is then silently missing from the scan.
- Consumers: `SecurityTraitCoverageTests.cs` (the inventory check against `docs/plans/simplification/security-test-inventory.txt`, line 288), `TestSuiteRatchetTests.cs`, `ModuleParityTests.cs` and `TestCoverageMapTests.cs`.
- `Microsoft.CodeAnalysis.CSharp` 5.12.0 is already referenced by `tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj:11`.

## 2. Options

| Option | Description | Verdict |
|---|---|---|
| A | Mask literals and comments. Lex the source with Roslyn (`SyntaxFactory.ParseTokens`, including trivia) and build a copy of the same length in which string, char and interpolated literal contents and comments become spaces. All window searches run on that copy. Names are still read from the original text at the same offsets. | **Recommended**: one helper; regex structure and offsets unchanged; minimal inventory churn. |
| B | Rewrite the scanner on the Roslyn syntax tree (`MethodDeclarationSyntax` plus `AttributeLists`). | Cleaner, but a bigger diff and a higher risk that inventory lines change for other reasons (nested types, records). Possible later. |
| C | Hand-written literal skipper. | Rejected: raw, verbatim and interpolated strings make it error-prone. |

## 3. Work package

**WP-909-1 (S, about 90 LOC including tests)**
- `tests/Autheris.Tests.Architecture/TestCoverageMapTests.cs`: add `internal static string MaskLiteralsAndComments(string source)`. Its result has the same length, and newlines are kept so the regexes and `RegexOptions.Multiline` still work. In `ExtractFromSource`, run `TypeDeclRegex`, `MethodDeclRegex`, the separator searches and `FactAttrRegex`/`SecurityTraitRegex` on the masked text.
  - Attribute arguments such as `"Category"` and `"Security"` are string literals, so masking would hide them from `SecurityTraitRegex`. Mask only for separator positions: compute `lastSep` on the masked window, then match the attribute regexes on the original text from that offset.
  - `FindTypeBodyEnd` (line 158) also uses the masked text for its brace counting.
- New tests in `tests/Autheris.Tests.Architecture/TestInventoryScannerTests.cs`:
  - `Theory_WithSemicolonInInlineData_IsFound`
  - `Theory_WithBracesInInlineData_IsFound`
  - `SecurityTrait_WithSemicolonInPrecedingComment_IsFound`
  - `RawStringLiteral_WithSeparators_DoesNotHideNextTest`
  - `Masking_PreservesLengthAndNewlines`
- Re-run `SecurityTraitCoverageTests`, `TestCoverageMapTests` and `TestSuiteRatchetTests`. If the fixed scanner now finds Security tests that were hidden, append them to `security-test-inventory.txt` and to `tests/Autheris.Tests.Architecture/Baselines/security-tests-base.txt`. Use `AUTHERIS_UPDATE_SECURITY_INVENTORY=1`, then review every added line by hand. A test may only be added, never removed.

No DDL. No dependencies. It conflicts only with PRs that touch the same scanner file.

## 4. Integration test impact

None. The change is in the architecture test project only.

## 5. Phase 3

**Not required.** No production code changes. The fix makes the existing security inventory check stricter.

## 6. Owner questions

None.
