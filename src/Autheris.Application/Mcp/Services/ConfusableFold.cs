namespace Autheris.Application.Mcp.Services;

/// <summary>
/// Folds the Cyrillic and Greek letters that look like Latin letters onto their Latin look-alike (MCP-09). Used only for prompt
/// injection inspection, after NFKC normalisation: NFKC does not unify scripts, so <c>ignоre</c> with a Cyrillic "о" would otherwise
/// escape the phrase patterns. The text that is inspected is never returned to a caller.
/// </summary>
internal static class ConfusableFold
{
    public static char Fold(char c) => c switch
    {
        // Cyrillic lower case
        'а' => 'a', 'е' => 'e', 'о' => 'o', 'р' => 'p', 'с' => 'c', 'у' => 'y', 'х' => 'x',
        'і' => 'i', 'ј' => 'j', 'ѕ' => 's', 'ԁ' => 'd', 'һ' => 'h', 'ѵ' => 'v', 'ԛ' => 'q',
        'ԝ' => 'w', 'к' => 'k', 'т' => 't', 'м' => 'm', 'н' => 'h', 'в' => 'b',
        // Cyrillic upper case
        'А' => 'A', 'В' => 'B', 'Е' => 'E', 'К' => 'K', 'М' => 'M', 'Н' => 'H', 'О' => 'O',
        'Р' => 'P', 'С' => 'C', 'Т' => 'T', 'У' => 'Y', 'Х' => 'X', 'І' => 'I', 'Ј' => 'J',
        'Ѕ' => 'S',
        // Greek lower case
        'ο' => 'o', 'α' => 'a', 'ρ' => 'p', 'ν' => 'v', 'ι' => 'i', 'κ' => 'k', 'τ' => 't',
        'υ' => 'u', 'χ' => 'x', 'ε' => 'e', 'η' => 'n', 'ω' => 'w',
        // Greek upper case
        'Α' => 'A', 'Β' => 'B', 'Ε' => 'E', 'Ζ' => 'Z', 'Η' => 'H', 'Ι' => 'I', 'Κ' => 'K',
        'Μ' => 'M', 'Ν' => 'N', 'Ο' => 'O', 'Ρ' => 'P', 'Τ' => 'T', 'Υ' => 'Y', 'Χ' => 'X',
        _ => c
    };
}
