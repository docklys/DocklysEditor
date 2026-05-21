using Avalonia.Controls;
using Docklys.ModuleContracts;

namespace hello
{
    public partial class hello : UserControl, IModule
    {
        // Identification
        public string Id => "hello";
        public string ModuleName => "hello";
        public string ModuleVersion => "1.0.0";
        public string Category => "Default";
        public string[] Tags => new [] { "hello", "example" };

        // Layout info — 1x1 = 110x110 tile (matches VolumeMixer's footprint).
        public int TileWidth => 1;
        public int TileHeight => 1;

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
        
        public hello()
        {
            InitializeComponent();
        }
    }
}