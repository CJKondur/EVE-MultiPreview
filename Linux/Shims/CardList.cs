// Stand-in so shared settings pages compile on Linux. S3 hides these settings or gives them a Linux implementation.
// The WPF list SectionVM.Cards makes for a look's section template. The Linux window draws the cards itself and never reads
// Cards; the members are only the ones SectionVM sets.
namespace EveMultiPreview.SettingsUi;

public sealed class CardList
{
    public object? ItemsSource { get; init; }
    public bool Focusable { get; init; }
}
