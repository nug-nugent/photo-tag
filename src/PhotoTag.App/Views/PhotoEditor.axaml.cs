using Avalonia.Controls;
using Avalonia.Threading;
using PhotoTag.App.ViewModels;

namespace PhotoTag.App.Views;

/// <summary>Editing one photo's tags, people, title, description and place, in the side panel and in the viewer.</summary>
public partial class PhotoEditor : UserControl
{
    public PhotoEditor()
    {
        InitializeComponent();
        // More tag suggestions while the tags are being edited. Not straight away: focus also moves when the
        // suggestions are being replaced (the focused one goes), and they can't be changed again mid-change.
        TagsSection.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsKeyboardFocusWithinProperty) Dispatcher.UIThread.Post(UpdateIsEditingTags);
        };
        // A new photo's details start with it off; the viewer and the side panel share them, so only the one with focus says.
        DataContextChanged += (_, _) =>
        {
            if (TagsSection.IsKeyboardFocusWithin) UpdateIsEditingTags();
        };
    }

    private void UpdateIsEditingTags()
    {
        if (DataContext is PhotoDetailsViewModel details) details.IsEditingTags = TagsSection.IsKeyboardFocusWithin;
    }
}
