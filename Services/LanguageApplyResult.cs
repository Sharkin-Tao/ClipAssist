namespace ClipAssist.Services;

/// <summary>Result of applying a language selection immediately from settings.</summary>
public sealed record LanguageApplyResult(bool Success, string? Error);