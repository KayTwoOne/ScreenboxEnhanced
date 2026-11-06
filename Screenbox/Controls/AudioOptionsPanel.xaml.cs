using Screenbox.Core.ViewModels;
using Windows.UI.Xaml.Controls;

namespace Screenbox.Controls;

/// <summary>
/// The audio options in the player: how far to shift audio against the picture.
/// </summary>
/// <remarks>
/// This is a control of its own so its markup can use compiled bindings. These options used to sit
/// in a flyout in the resources of <see cref="PlayerControls"/>, where only <c>Binding</c> can be
/// used, and Native AOT breaks <c>Binding</c>.
/// </remarks>
public sealed partial class AudioOptionsPanel : UserControl
{
    internal PlayerControlsViewModel? ViewModel { get; init; }

    public AudioOptionsPanel()
    {
        this.InitializeComponent();
    }
}
