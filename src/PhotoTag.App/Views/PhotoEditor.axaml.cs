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
        // More tag suggestions while the tags are being edited: from when the Tags box gets focus until focus leaves
        // the Tags section (so moving on to a suggestion keeps them). A suggestion getting focus doesn't start it:
        // a press focuses the button, and replacing the list under the pointer would lose the click. Not straight
        // away either: focus also moves when the suggestions are being replaced (the focused one goes), and they
        // can't be changed again mid-change.
        TagsSection.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsKeyboardFocusWithinProperty) Dispatcher.UIThread.Post(UpdateIsEditingTags);
        };
        NewTagBox.PropertyChanged += (_, e) =>
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
        if (DataContext is not PhotoDetailsViewModel details) return;
        if (!TagsSection.IsKeyboardFocusWithin) details.IsEditingTags = false;
        else if (NewTagBox.IsKeyboardFocusWithin) details.IsEditingTags = true;
    }
}
