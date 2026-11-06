// Isolated receiver only. Never references or loads a running RutonyChat process.
namespace RutonyChat;
public static class ProgramProps { public enum SiteEnum { other } }
public static class CurrencyControl
{
    public sealed class CurrencyItem { public string CharCode; }
    public sealed class SumClass(float sum, CurrencyItem currency) { public readonly float sum = sum; public readonly CurrencyItem currency = currency; }
}
public static class RutonyBot
{
    public static readonly List<string> Messages = [];
    public static void SayToWindow(string text) => Messages.Add(text);
}
public static class ChatEventServer
{
    public static event Action<ProgramProps.SiteEnum, string, CurrencyControl.SumClass, string, string> OnDonate;
    public static string LastId;
    public static CurrencyControl.SumClass LastSum;
    public static void Donate(ProgramProps.SiteEnum site, string name, CurrencyControl.SumClass sum, string text, bool test, string voice, string id)
    {
        if (test || voice != "") throw new Exception("Wrong native flags");
        LastId = id; LastSum = sum; OnDonate?.Invoke(site, name, sum, text, text);
    }
}

public static class RutonyBotFunctions
{
    public static string RegisteredDirectory;
    public static bool ThrowOnLookup;
    public static readonly List<string> Lookups = new();
    public static string GetScriptDirectory(string scriptname)
    {
        Lookups.Add(scriptname);
        if (ThrowOnLookup) throw new InvalidOperationException("Synthetic SDK lookup failure");
        return RegisteredDirectory;
    }
}
