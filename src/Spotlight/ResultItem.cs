using System.ComponentModel;
using System.Windows.Media;
using Spotlight.Search;

namespace Spotlight;

/// <summary>One row in the results list. The icon arrives a moment later from <see cref="Services.IconCache"/>.</summary>
public sealed class ResultItem : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public ResultItem(SearchHit hit) => Hit = hit;

    public SearchHit Hit { get; }
    public string Title => Hit.Title;
    public string Subtitle => Hit.Subtitle;
    public string KindLabel => Hit.KindLabel;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
