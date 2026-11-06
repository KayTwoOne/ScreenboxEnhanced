using Screenbox.Core.ViewModels;
using Windows.UI.Xaml.Controls;

namespace Screenbox.Controls;

/// <summary>
/// The subtitle options in the player: timing offset and size.
/// </summary>
/// <remarks>
/// This is a control of its own so its markup can use compiled bindings. These options used to sit
/// in a flyout in the resources of <see cref="PlayerControls"/>, where only <c>Binding</c> can be
/// used, and Native AOT breaks <c>Binding</c>.
/// </remarks>
public sealed partial class SubtitleOptionsPanel : UserControl
{
    internal PlayerControlsViewModel? ViewModel { get; init; }

    public SubtitleOptionsPanel()
    {
        this.InitializeComponent();
    }
}
