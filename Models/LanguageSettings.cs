namespace ClipAssist.Models;

/// <summary>
/// Persisted language preference. A null/empty Mode means the user has not
/// chosen a language yet, which triggers the first-run language window.
/// </summary>
public sealed class LanguageSettings
{
    public string? Mode { get; set; }
}