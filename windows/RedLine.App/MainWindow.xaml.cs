using Microsoft.UI.Xaml;
using RedLine.Core;

namespace RedLine.App;

/// <summary>
/// A first window over the engine's published snapshot. Every string on it comes from
/// TrayPresenter, which is where the tests are, so this file stays free of judgement about
/// what a number means.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly Engine engine = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "RedLine";
        Refresh();
    }

    private void Refresh()
    {
        var view = TrayPresenter.From(engine.ReadSnapshot(), DateTimeOffset.UtcNow);
        TitleText.Text = view.Title;
        PhraseText.Text = view.Phrase;
        DetailText.Text = view.Detail;
    }
}
