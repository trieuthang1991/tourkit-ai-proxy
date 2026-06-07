// Services/Chat/AgentGuardrails.cs
using System.Text.RegularExpressions;
using System.Globalization;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Chat;

public static class AgentGuardrails
{
    // ---------------------------------------------------------------
    // StripEmDash: thay em-dash (U+2014) + en-dash (U+2013) thanh hyphen
    // ---------------------------------------------------------------

    /// Xoa em-dash va en-dash, thay bang hyphen thuong.
    public static string StripEmDash(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? "";
        return input.Replace('—', '-').Replace('–', '-');
    }
}
