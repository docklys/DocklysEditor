using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Todo.Models;
using Todo.Utils;

namespace Todo.ViewModels
{
    public class TodoEditViewModel : INotifyPropertyChanged
    {
        private readonly TodoViewModel _parent;
        private readonly TodoItem _item;

        public string Title { get => _item.Title; set { _item.Title = value; OnPropertyChanged(); } }
        public string Description { get => _item.Description; set { _item.Description = value; OnPropertyChanged(); } }
        public string Category { get => _item.Category; set { _item.Category = value; OnPropertyChanged(); } }
        public string Priority { get => _item.Priority; set { _item.Priority = value; OnPropertyChanged(); } }

        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }

        public TodoEditViewModel(TodoViewModel parent, TodoItem item)
        {
            _parent = parent;
            _item = item;
            SaveCommand = new RelayCommand(_ => Save());
            CancelCommand = new RelayCommand(_ => Cancel());
        }

        private void Save()
        {
            _parent.ApplyFilter();
        }

        private void Cancel()
        {
            // no-op
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
