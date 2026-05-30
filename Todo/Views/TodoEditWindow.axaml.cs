using Avalonia.Controls;
using Todo.Models;
using Todo.ViewModels;

namespace Todo.Views
{
    public partial class TodoEditWindow : Window
    {
        public TodoEditWindow()
        {
            InitializeComponent();
        }

        public TodoEditWindow(TodoViewModel parent, TodoItem item) : this()
        {
            DataContext = new TodoEditViewModel(parent, item);
        }
    }
}
