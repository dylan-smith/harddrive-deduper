using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DupeHunter.Gui.ViewModels;

namespace DupeHunter.Gui.Views;

/// <summary>
/// Reusable master/detail for one duplicate-set list: a virtualized grid of groups over a pane of
/// the selected group's member locations with their per-copy actions.
/// </summary>
public partial class GroupsView : UserControl
{
    public static readonly DependencyProperty GroupsProperty = DependencyProperty.Register(
        nameof(Groups), typeof(ICollectionView), typeof(GroupsView));

    public GroupsView() => InitializeComponent();

    public ICollectionView? Groups
    {
        get => (ICollectionView?)GetValue(GroupsProperty);
        set => SetValue(GroupsProperty, value);
    }

    private async void GroupsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupsGrid.SelectedItem is DuplicateGroupViewModel group)
        {
            // Lazily fetch the group's full member list the first time it's opened.
            await group.EnsureMembersLoadedAsync();
        }
    }
}
