using Avalonia.Controls;
using Avalonia.Interactivity;
using Docklys.ModuleContracts;
using Todo.ViewModels;
using Todo.Models;
using Todo.Views;

namespace Todo
{
    public partial class Todo : UserControl, IModule
    {
        // Identification
        public string Id => "Todo";
        public string ModuleName => "Todo";
        public string ModuleVersion => "1.0.0";
        public string Category => "Default";
        public string[] Tags => new [] { "Todo", "example" };

        // Layout info — 1x1 = 110x110, matching VolumeMixer's footprint.
        public int TileWidth => 2;
        public int TileHeight => 3;

        // Compatibility
        public string MinAppVersion => "1.0.0";
        public string MaxAppVersion => "2.0.0";
        public string[] SupportedPlatforms => new [] { "Windows", "Linux", "Mac" };
        
        // Unique Module ID (set by the main app)
        private string _uniqueModuleId;
        public string UniqueModuleId { get { return _uniqueModuleId; } }

        public void SetModuleId(string uniqueModuleId)
        {
            _uniqueModuleId = uniqueModuleId;
        }

        public void PrintModuleId()
        {
            Console.WriteLine($"Module ID: {UniqueModuleId}");
        }
        
        private TodoViewModel _vm;

        public Todo()
        {
            InitializeComponent();
            _vm = new TodoViewModel();
            DataContext = _vm;

            // Wire Add button
            AddButton.Click += AddButton_Click;
        }

        private async void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.DataContext is TodoItem item)
            {
                var dlg = new TodoEditWindow(_vm, item);
                await dlg.ShowDialog((Window) this.VisualRoot);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.DataContext is TodoItem item)
            {
                _vm.DeleteCommand.Execute(item);
            }
        }

        private void CycleStatus_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.DataContext is TodoItem item)
            {
                _vm.CycleStatusCommand.Execute(item);
            }
        }

        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            _vm.AddCommand.Execute(null);
            if (_vm.SelectedItem != null)
            {
                var dlg = new TodoEditWindow(_vm, _vm.SelectedItem);
                await dlg.ShowDialog((Window) this.VisualRoot);
            }
        }
    }
}