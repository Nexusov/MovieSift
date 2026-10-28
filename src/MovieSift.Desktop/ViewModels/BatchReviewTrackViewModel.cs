namespace MovieSift.Desktop.ViewModels;

/// <summary>Describes one immutable plan track for compact presentation, never an editable Keep choice.</summary>
public sealed record BatchReviewTrackViewModel(int Identifier, string IdentityText, string TitleText)
{
    public bool HasTitle => TitleText.Length != 0;
}
