namespace Igloo.Preflight;


internal static class WmiValues
{
    internal static char ToDriveLetter(object? value) => value switch
    {
        char c => c,
        ushort u when u > 0 => (char)u,
        string s when s.Length > 0 => s[0],
        _ => '\0',
    };
}
