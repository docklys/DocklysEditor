using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace Todo.Models
{
    public class TodoItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string Status { get; set; }
        public string Priority { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Helper properties for UI binding
        public string ShortDescription => string.IsNullOrWhiteSpace(Description) ? "" : (Description.Length > 120 ? Description.Substring(0, 117) + "…" : Description);
        public IBrush StatusDot
        {
            get
            {
                return Status switch
                {
                    "idea" => Brushes.Gray,
                    "todo" => Brushes.Blue,
                    "inprogress" => Brushes.Orange,
                    "done" => Brushes.Green,
                    _ => Brushes.Gray
                };
            }
        }

        public IBrush PriorityColor
        {
            get
            {
                return Priority switch
                {
                    "high" => Brushes.Red,
                    "medium" => Brushes.Orange,
                    "low" => Brushes.Gray,
                    _ => Brushes.Gray
                };
            }
        }
    }
}
