using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Helpers;
using Screenbox.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace Screenbox.Pages;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class PlayQueuePage : Page
{
    internal PlayQueuePageViewModel ViewModel => (PlayQueuePageViewModel)DataContext;

    internal CommonViewModel Common { get; }

    public PlayQueuePage()
    {
        this.InitializeComponent();
        DataContext = Ioc.Default.GetRequiredService<PlayQueuePageViewModel>();
        Common = Ioc.Default.GetRequiredService<CommonViewModel>();
    }

    private async void PlayQueuePage_OnLoaded(object sender, RoutedEventArgs e)
    {
        // async void: scrolling the active item into view is cosmetic, so a failure here must not
        // end the process, which is what an exception escaping an async void handler does.
        try
        {
            await PlayQueue.SmoothScrollActiveItemIntoViewAsync();
        }
        catch (Exception ex)
        {
            DefaultLogging.CreateLogger<PlayQueuePage>().LogError(ex, "Failed to scroll the active queue item into view.");
        }
    }
}
