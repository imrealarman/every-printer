using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace EveryPrinter;

/// <summary>One entry in the sheet strip: its caption, its thumbnail (filled in lazily) and whether it is the sheet being viewed.</summary>
public sealed class SheetItem(int index, string caption, double thumbHeight) : INotifyPropertyChanged
{
    private ImageSource? thumb;
    private bool selected;

    public int Index { get; } = index;
    public string Caption { get; set; } = caption;
    public double ThumbHeight { get; set; } = thumbHeight;

    /// <summary>True while the list has a container for this item, so only visible sheets are rendered.</summary>
    public bool Realized { get; set; }

    public ImageSource? Thumb
    {
        get => thumb;
        set { thumb = value; Raise(nameof(Thumb)); }
    }

    public bool Selected
    {
        get => selected;
        set { selected = value; Raise(nameof(Border)); }
    }

    public Brush Border => selected ? (Brush)Application.Current.Resources["AccentBrush"] : (Brush)Application.Current.Resources["TileBrush"];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
