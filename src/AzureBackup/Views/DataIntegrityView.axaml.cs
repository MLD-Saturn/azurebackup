using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AzureBackup.Views;

public partial class DataIntegrityView : UserControl
{
    public DataIntegrityView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
