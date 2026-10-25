using System.Windows;
using System.Windows.Automation;
using ClipAssist.Localization;

namespace ClipAssist.Views;

/// <summary>
/// First-run language picker. It previews the selected language immediately
/// but does not change the application language until the user confirms.
/// Closing the window returns null and the caller keeps the system language.
/// </summary>
public partial class LanguageSelectionWindow : Window
{
    private readonly LocalizationService _localization = LocalizationService.Instance;
    private bool _initializing = true;

    public LanguageSelectionWindow(LanguagePreference suggested)
    {
        InitializeComponent();

        SelectedLanguage = suggested;
        ChineseOption.IsChecked = suggested == LanguagePreference.ChineseSimplified;
        EnglishOption.IsChecked = suggested == LanguagePreference.English;
        _initializing = false;

        // Checked events may fire while the fields are being initialized; make
        // sure the preview reflects the final selection exactly once.
        ApplyPreview();

        Loaded += (_, _) =>
            (SelectedLanguage == LanguagePreference.English ? EnglishOption : ChineseOption).Focus();
    }

    public LanguagePreference SelectedLanguage { get; private set; }

    private void OnLanguageChecked(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        SelectedLanguage = EnglishOption.IsChecked == true
            ? LanguagePreference.English
            : LanguagePreference.ChineseSimplified;
        ApplyPreview();
    }

    private void ApplyPreview()
    {
        var language = SelectedLanguage;
        Title = _localization.Get(language, "Language.Title");
        HeadingText.Text = Title;
        PromptText.Text = _localization.Get(language, "Language.Prompt");
        NoteText.Text = _localization.Get(language, "Language.Note");
        ConfirmButton.Content = _localization.Get(language, "Language.Confirm");
        AutomationProperties.SetName(ConfirmButton, _localization.Get(language, "Language.Confirm"));
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}