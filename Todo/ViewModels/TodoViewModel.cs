using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Collections.Generic;
using Todo.Models;
using Todo.Utils;

namespace Todo.ViewModels
{
    public class TodoViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<TodoItem> Items { get; } = new ObservableCollection<TodoItem>();

        private List<TodoItem> _filtered = new List<TodoItem>();
        public IEnumerable<TodoItem> FilteredItems => _filtered;

        public List<string> Categories { get; } = new List<string> { "All", "pipeline", "infra", "agent", "business" };
        public List<string> Statuses { get; } = new List<string> { "All", "idea", "todo", "inprogress", "done" };

        private string _selectedCategory = "All";
        public string SelectedCategory { get => _selectedCategory; set { _selectedCategory = value; OnPropertyChanged(); ApplyFilter(); } }

        private string _selectedStatus = "All";
        public string SelectedStatus { get => _selectedStatus; set { _selectedStatus = value; OnPropertyChanged(); ApplyFilter(); } }

        private TodoItem? _selectedItem;
        public TodoItem? SelectedItem { get => _selectedItem; set { _selectedItem = value; OnPropertyChanged(); } }

        public ICommand AddCommand { get; }
        public ICommand EditCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand CycleStatusCommand { get; }

        public TodoViewModel()
        {
            // Seed
            Items.Add(new TodoItem { Title = "Example todo", Description = "This is an example item.", Category = "pipeline", Status = "idea", Priority = "high", Tags = new List<string>{ "example", "seed" } });
            Items.Add(new TodoItem { Title = "Second item", Description = "Another item.", Category = "agent", Status = "todo", Priority = "medium", Tags = new List<string>{ "sample" } });

            AddCommand = new RelayCommand(_ => AddNew());
            EditCommand = new RelayCommand(p => SelectedItem = p as TodoItem, p => p is TodoItem);
            DeleteCommand = new RelayCommand(p => DeleteItem(p as TodoItem), p => p is TodoItem);
            CycleStatusCommand = new RelayCommand(p => CycleStatus(p as TodoItem), p => p is TodoItem);

            ApplyFilter();
        }

        private void AddNew()
        {
            var t = new TodoItem { Title = "New todo", Description = "", Category = "pipeline", Status = "idea", Priority = "medium" };
            Items.Add(t);
            ApplyFilter();
            SelectedItem = t;
        }

        private void DeleteItem(TodoItem? item)
        {
            if (item == null) return;
            Items.Remove(item);
            if (SelectedItem == item) SelectedItem = null;
            ApplyFilter();
        }

        private void CycleStatus(TodoItem? item)
        {
            if (item == null) return;
            var order = new[] { "idea", "todo", "inprogress", "done" };
            var idx = Array.IndexOf(order, item.Status);
            if (idx < 0) idx = 0;
            idx = (idx + 1) % order.Length;
            item.Status = order[idx];
            ApplyFilter();
            OnPropertyChanged(nameof(Items));
        }

        public void ApplyFilter()
        {
            var q = Items.AsEnumerable();
            if (SelectedCategory != "All") q = q.Where(i => i.Category == SelectedCategory);
            if (SelectedStatus != "All") q = q.Where(i => i.Status == SelectedStatus);
            _filtered = q.ToList();
            OnPropertyChanged(nameof(FilteredItems));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
